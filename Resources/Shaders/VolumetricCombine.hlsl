//////////////////////////////////////////////////////////////////////////
// This shader finally combines the volumetric rendering with the actual screen
//
#include "Inc/Global.hlsl"
#include "Inc/Volumetric.hlsl"
#include "Inc/Atmosphere.hlsl"

Texture2DArray	_TexAtmosphere				: register(t10);
Texture2D		_TexSceneDepth				: register(t11);
Texture2D		_TexDownsampledSceneDepth	: register(t12);
Texture2D		_TexScene					: register(t13);

//[
cbuffer	cbObject	: register( b10 )
{
	float3		_dUV;
};
//]

struct	VS_IN
{
	float4	__Position	: SV_POSITION;
};

VS_IN	VS( VS_IN _In )	{ return _In; }



float3	HDR( float3 L, float _Exposure=0.5 )
{
	L = L * _Exposure;
	L = 1.0 - exp( -L );
	return L;
}

// If we're outputting to a non sRGB render target...
float3	HDR_sRGB( float3 L, float _Exposure=0.5 )
{
	L = L * _Exposure;
	L.x = L.x < 1.413 ? pow( L.x * 0.38317, 1.0 / 2.2 ) : 1.0 - exp( -L.x );
	L.y = L.y < 1.413 ? pow( L.y * 0.38317, 1.0 / 2.2 ) : 1.0 - exp( -L.y );
	L.z = L.z < 1.413 ? pow( L.z * 0.38317, 1.0 / 2.2 ) : 1.0 - exp( -L.z );
	return L;
}


// Read Z from the ZBuffer
float	ReadDepth( float2 _UV )
{
	float	Zproj = _TexSceneDepth.SampleLevel( LinearClamp, _UV, 0.0 ).x;

	float	Q = _CameraData.w / (_CameraData.w - _CameraData.z);	// Zf / (Zf-Zn)
	return (Q * _CameraData.z) / (Q - Zproj);
}

void	UpSampleAtmosphere( float2 _UV, out float3 _Scattering, out float3 _Extinction )
{
	float3	DowndUV = 2.0 * _dUV;	// Size of a pixel in the downsampled map (downsample factor = 0.5)

	float2	uv = (_UV.xy - _dUV.xy) / DowndUV.xy;
	float2	DownPixel = floor( uv );
			uv -= DownPixel;						// Default UV interpolants for a normal bilinear interpolation

	float2	UV = DownPixel * DowndUV.xy + _dUV.xy;

	float	CenterZ = ReadDepth( UV + _dUV.xy );	// The Z at which the values scattering & extinction were computed => exactly the center

	float3	Scattering[4], Extinction[4];
	float4	CornerZ;
	CornerZ.x = ReadDepth( UV );	Scattering[0] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 0 ), 0.0 ).xyz;	Extinction[0] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 1 ), 0.0 ).xyz;	UV.x += DowndUV.x;
	CornerZ.y = ReadDepth( UV );	Scattering[1] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 0 ), 0.0 ).xyz;	Extinction[1] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 1 ), 0.0 ).xyz;	UV.y += DowndUV.y;
	CornerZ.z = ReadDepth( UV );	Scattering[2] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 0 ), 0.0 ).xyz;	Extinction[2] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 1 ), 0.0 ).xyz;	UV.x -= DowndUV.x;
	CornerZ.w = ReadDepth( UV );	Scattering[3] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 0 ), 0.0 ).xyz;	Extinction[3] = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 1 ), 0.0 ).xyz;	UV.y -= DowndUV.y;

	// Compute bias weights toward each sample based on Z discrepancies
	const float		WeightFactor = 2.0;
	const float		ZInfluenceFactor = 0.001;
	float4	DeltaZ = 0;//ZInfluenceFactor * abs(CenterZ - CornerZ);
	float4	Weights = saturate( WeightFactor / (1.0 + DeltaZ) );

	// This vector gives the bias toward one of the UV corners. It lies in [-1,+1]
	// For equal weights, the bias sums to 0 so the UVs won't be influenced and normal bilinear filtering is applied
	// Otherwise, the UVs will tend more or less strongly toward one of the corners of the low-resolution pixel where values were sampled
	//
	// Explicit code would be :
	// float2	uv_bias  = Weights.x * float2( -1.0, -1.0 )		// Bias toward top-left
	// 					 + Weights.y * float2( +1.0, -1.0 )		// Bias toward top-right
	// 					 + Weights.z * float2( +1.0, +1.0 )		// Bias toward bottom-right
	// 					 + Weights.w * float2( -1.0, +1.0 );	// Bias toward bottom-left
	float2	uv_bias  = float2( Weights.y + Weights.z - Weights.x - Weights.w, Weights.z + Weights.w - Weights.x - Weights.y );

	// Now, we need to apply the actual UV bias.
	//
	// Explicit code would be :
	// 	uv.x = uv_bias.x < 0.0 ? lerp( uv.x, 0.0, -uv_bias.x ) : lerp( uv.x, 1.0, uv_bias.x );
	// 	uv.y = uv_bias.y < 0.0 ? lerp( uv.y, 0.0, -uv_bias.y ) : lerp( uv.y, 1.0, uv_bias.y );
	//
	// Unfortunately, using branching 1) is bad and 2) yields some infinite values for some obscure reason !
	// So we need to remove the branching.
	// The idea here is to perform biasing toward top-left & bottom-right independently then choose which bias direction
	//	is actually needed, based on the sign of the uv_bias vector
	//
	float2	uv_topleft = lerp( uv, 0.0, saturate(-uv_bias) );	// Bias toward top-left corner (works if uv_bias is negative)
	float2	uv_bottomright = lerp( uv, 1.0, saturate(uv_bias) );	// Bias toward bottom-right corner (works if uv_bias is positive)
	float2	ChooseDirection = saturate( 10000.0 * uv_bias );	// Isolate the sign of the uv_bias vector so negative gives 0 and positive gives 1
	uv = lerp( uv_topleft, uv_bottomright, ChooseDirection );	// Final bias will choose the appropriate direction based on the sign of the bias


	// Perform normal bilinear filtering with biased UV interpolants
	_Scattering = lerp(	lerp( Scattering[0], Scattering[1], uv.x ),
						lerp( Scattering[2], Scattering[3], uv.x ),
						uv.y );

	_Extinction = lerp(	lerp( Extinction[0], Extinction[1], uv.x ),
						lerp( Extinction[2], Extinction[3], uv.x ),
						uv.y );
}

float3	PS( VS_IN _In ) : SV_TARGET0
{
	float2	UV = _In.__Position.xy * _dUV.xy;

// return 1.0 * _TexScattering.SampleLevel( LinearClamp, float3( UV, 0.5 * (1.0 + sin( _Time.x )) ), 0.0 ).xyz;
// return 100.0 * _TexIrradiance.SampleLevel( LinearClamp, UV, 0.0 ).xyz;
// return exp( -1.0 * _TexTransmittance.SampleLevel( LinearClamp, UV, 0.0 ).xyz );

// {
// 	float	_AltitudeKm = ATMOSPHERE_THICKNESS_KM * frac( 0.25 * _Time.x );
// 	float	r = GROUND_RADIUS_KM + _AltitudeKm;
// 	const float	H = sqrt( ATMOSPHERE_RADIUS_KM * ATMOSPHERE_RADIUS_KM - GROUND_RADIUS_KM * GROUND_RADIUS_KM );
// 	float	rho = sqrt( r * r - GROUND_RADIUS_KM * GROUND_RADIUS_KM );
// 	return rho / H;
// 	return step( 1.0, rho / H );
// }


#if 0
// Test downsampled depth buffer
float3	Bisou = _TexDownsampledSceneDepth.SampleLevel( PointClamp, UV, 1.0 ).xyz;
return 0.01 * Bisou.z;
return Bisou.y > Bisou.x ? float3( 1, 0, 0 ) : float3( 0, 0, 0 );
return Bisou.z < Bisou.x ? float3( 1, 0, 0 ) : float3( 0, 0, 0 );
return 0.01 * (Bisou.z - Bisou.x);
return 0.01 * (Bisou.x - Bisou.y);
#endif

// DEBUG
#if 0
if ( UV.x < 0.3 && UV.y > 0.7 )
{	// Show the transmittance map
	UV.x /= 0.3;
	UV.y = (UV.y - 0.7) / 0.3;

	float3	UVW = float3( UV, 0.5 * (1.0 + sin( _Time.x )) );

	if ( UV.x > 0.99 ) return float4( UVW.z, 0, 0, 0 );

// 	float	r = GROUND_RADIUS_KM + WORLD2KM * _Camera2World[3].y;
// 	float	h = sqrt( r * r - GROUND_RADIUS_KM * GROUND_RADIUS_KM );
// 	const float	H = sqrt( ATMOSPHERE_RADIUS_KM * ATMOSPHERE_RADIUS_KM - GROUND_RADIUS_KM * GROUND_RADIUS_KM );
// 	float	uAltitude = 0.5 / RESOLUTION_ALTITUDE + (h / H) * NORMALIZED_SIZE_W;
// 	return 1.0 * _TexScattering.SampleLevel( LinearClamp, float3( UV, uAltitude ), 0.0 ).xyz;


// float	_CosThetaSun = lerp( -0.2, 1.0, UV.x );
// float	Bisou = 0.5 / RESOLUTION_COS_THETA_SUN + (atan( max( _CosThetaSun, -0.1975 ) * tan( 1.26 * 1.1 ) ) / 1.1 + (1.0 - 0.26)) * 0.5 * NORMALIZED_SIZE_U1;
// //float	Bisou = 0.5 / RESOLUTION_COS_THETA_SUN + max( 0.0, (1.0 - exp( -3.0 * _CosThetaSun - 0.6 )) / (1.0 - exp(-3.6)) ) * NORMALIZED_SIZE_U1;
// return Bisou;



//	return 1.0 * abs(_TexScattering.SampleLevel( LinearClamp, UVW, 0.0 ).xyz);
// 	return 20.0 * _TexIrradiance.SampleLevel( LinearClamp, UV, 0.0 ).xyz;
// 	return _TexTransmittance.SampleLevel( LinearClamp, UV, 0.0 ).xyz;

// UVW.z = 0.9;

 	return 1.0 * _TexTransmittance_Limited.SampleLevel( LinearClamp, UVW, 0.0 ).xyz;
//	return _TexCloudTransmittance.SampleLevel( LinearClamp, float3( UV, 0 ), 0.0 ).xyz;
//	return _TexTerrainShadow.SampleLevel( LinearClamp, UV, 0.0 ).xyz;


// float	AltitudeKm = UV.y * ATMOSPHERE_THICKNESS_KM;
// float	CosThetaSun = lerp( -0.2, 1.0, UV.x );
// float	Reflectance = saturate( CosThetaSun );
// return float4( 1.0 * GetTransmittance( AltitudeKm, CosThetaSun ) * Reflectance, 0.0 );	// Return Sun reflectance attenuated by atmosphere as seen from given altitude

}
#endif
// DEBUG


	float3	View = normalize( float3( _CameraData.x * (2.0 * UV.x - 1.0), -_CameraData.y * (2.0 * UV.y - 1.0), 1.0 ) );
			View = mul( float4( View, 0.0 ), _Camera2World ).xyz;

	// Load terrain background
	float4	TerrainAlpha = _TexScene.SampleLevel( LinearClamp, UV, 0.0 );
	float3	Terrain = TerrainAlpha.xyz;
//return TerrainAlpha.w;
//return HDR( Terrain );

	// Load scattering & extinction from sky and clouds
#if 1
	float3	Scattering = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 0 ), 0.0 ).xyz;
	float3	Extinction = _TexAtmosphere.SampleLevel( LinearClamp, float3( UV, 1 ), 0.0 ).xyz;
#else
	float3	Scattering, Extinction;
	UpSampleAtmosphere( UV, Scattering, Extinction );
#endif
//return Scattering;
//return abs(Extinction);
//return HDR( Scattering );
//return HDR( Extinction );

//Extinction = pow( Extinction, _ExtinctionBoost );
// return Extinction;


	// Compute Sun's color
	float	CameraAltitudeKm = WORLD2KM * _Camera2World[3].y;
	float	CosThetaView = View.y;
	float	CosGamma = dot( View, _LightDirection );

	float3	SunColor = SUN_INTENSITY * GetTransmittance( CameraAltitudeKm, CosThetaView );	// Attenuated through the atmosphere
	float3	DirectSunLight = smoothstep( 0.9997, 0.9999, CosGamma );							// 1 if we're looking directly at the Sun (warning: bad for the eyes!)
			DirectSunLight *= (1.0-TerrainAlpha.w) * SunColor;
//return HDR( DirectSunLight );

	// Compose color
	float3	FinalColor = (Terrain + DirectSunLight) * Extinction + Scattering;

// 	// Add a nice bloom for the Sun
// 	FinalColor += 0.02 * smoothstep( 0.9, 1.0, sqrt(CosGamma) ) * SunColor * smoothstep( 0.1, 0.3, _LightDirection.y );
// 	FinalColor += 0.002 * smoothstep( 0.1, 1.0, sqrt(CosGamma) ) * SunColor * smoothstep( 0.1, 0.3, _LightDirection.y );

	return HDR( FinalColor );
}