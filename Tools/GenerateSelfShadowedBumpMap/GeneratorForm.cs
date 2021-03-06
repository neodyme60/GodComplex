﻿//////////////////////////////////////////////////////////////////////////
// Implements the Valve technique for self-shadowed bump maps
// Source: http://www.valvesoftware.com/publications/2007/SIGGRAPH2007_EfficientSelfShadowedRadiosityNormalMapping.pdf
// More: http://n00body.squarespace.com/journal/2010/2/7/self-shadowed-bump-maps.html
//
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

using RendererManaged;

namespace GenerateSelfShadowedBumpMap
{
	public partial class GeneratorForm : Form
	{
		#region CONSTANTS

		private const int		MAX_THREADS = 1024;			// Maximum threads run by the compute shader

		private const int		BILATERAL_PROGRESS = 50;	// Bilateral filtering is considered as this % of the total task (bilateral is quite long so I decided it was equivalent to 50% of the complete computation task)
		private const int		MAX_LINES = 16;				// Process at most that amount of lines of a 4096x4096 image for a single dispatch

		#endregion

		#region NESTED TYPES

		[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
		private struct	CBInput {
			public UInt32	Y0;					// Index of the texture line we're processing
			public UInt32	RaysCount;			// Amount of rays in the structured buffer
			public UInt32	MaxStepsCount;		// Maximum amount of steps to take before stopping
			public UInt32	Tile;				// Tiling flag
			public float	TexelSize_mm;		// Size of a texel (in millimeters)
			public float	Displacement_mm;	// Max displacement value encoded by the height map (in millimeters)
		}

		[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
		private struct	CBFilter {
			public UInt32	Y0;					// Index of the texture line we're processing
			public float	Radius;				// Radius of the bilateral filter
			public float	Tolerance;			// Range tolerance of the bilateral filter
			public UInt32	Tile;				// Tiling flag
		}

		[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
		public struct	CBDisplay {
			public uint		_Width;
			public uint		_Height;
			public float	_Time;
			public uint		_Flags;

			public float3	_Light;
			public float	_Height_mm;

			public float3	_CameraPos;
			public float	_Size_mm;

			public float3	_CameraTarget;
			float			__PAD;

			public float3	_CameraUp;
			float			__PAD2;
		}

		#endregion

		#region FIELDS

		private RegistryKey						m_AppKey;
		private string							m_ApplicationPath;

		private ViewerForm						m_viewerForm;

		private System.IO.FileInfo				m_SourceFileName = null;
		private int								W, H;
		private ImageUtility.Bitmap				m_BitmapSource = null;

		internal Device							m_Device = new Device();
		internal Texture2D						m_TextureSource = null;
		internal Texture2D						m_TextureTarget0 = null;
		internal Texture2D						m_TextureTarget1 = null;
		internal Texture2D						m_TextureTarget_CPU = null;

		// SSBump Generation
		private ConstantBuffer<CBInput>			m_CB_Input;
		private StructuredBuffer<float3>		m_SB_Rays = null;
		private ComputeShader					m_CS_GenerateSSBumpMap = null;

		// Bilateral filtering pre-processing
		private RendererManaged.ConstantBuffer<CBFilter>	m_CB_Filter;
		private RendererManaged.ComputeShader	m_CS_BilateralFilter = null;

		// Display
		public ConstantBuffer<CBDisplay>		m_CB_Display;
		public Shader							m_PS_Display;

		private ImageUtility.ColorProfile		m_LinearProfile = new ImageUtility.ColorProfile( ImageUtility.ColorProfile.Chromaticities.sRGB, ImageUtility.ColorProfile.GAMMA_CURVE.STANDARD, 1.0f );
		private ImageUtility.Bitmap				m_BitmapResult = null;

		#endregion

		#region PROPERTIES

		internal float	TextureHeight_mm {
			get { return 10.0f * floatTrackbarControlHeight.Value; }
		}

		internal float	TextureSize_mm {
			get { return 10.0f * floatTrackbarControlPixelDensity.Value; }
		}

		#endregion

		#region METHODS

		public unsafe GeneratorForm()
		{
			InitializeComponent();

			m_viewerForm = new ViewerForm( this );

// Remove unused tabs until we make them work
tabControlGenerators.TabPages.RemoveAt( 1 );

 			m_AppKey = Registry.CurrentUser.CreateSubKey( @"Software\GodComplex\SSBumpMapGenerator" );
			m_ApplicationPath = System.IO.Path.GetDirectoryName( Application.ExecutablePath );

			#if DEBUG
				buttonReload.Visible = true;
			#endif
		}

		protected override void  OnLoad(EventArgs e)
		{
 			base.OnLoad(e);

			try {
				m_Device.Init( m_viewerForm.Handle, false, true );

				// Create our compute shaders
				#if DEBUG
					m_CS_BilateralFilter = new ComputeShader( m_Device, new ShaderFile( new System.IO.FileInfo( "./Shaders/BilateralFiltering.hlsl" ) ), "CS", null );
					m_CS_GenerateSSBumpMap = new ComputeShader( m_Device, new ShaderFile( new System.IO.FileInfo( "./Shaders/GenerateSSBumpMap.hlsl" ) ), "CS", null );
					m_PS_Display = new Shader( m_Device, new ShaderFile( new System.IO.FileInfo( "./Shaders/Display.hlsl" ) ), VERTEX_FORMAT.Pt4, "VS", null, "PS", null );
				#else
					m_CS_BilateralFilter = ComputeShader.CreateFromBinaryBlob( m_Device, new System.IO.FileInfo( "./Shaders/Binary/BilateralFiltering.fxbin" ), "CS" );
					m_CS_GenerateSSBumpMap = ComputeShader.CreateFromBinaryBlob( m_Device, new System.IO.FileInfo( "./Shaders/Binary/GenerateSSBumpMap.fxbin" ), "CS" );
					m_PS_Display = Shader.CreateFromBinaryBlob( m_Device, new System.IO.FileInfo( "./Shaders/Display.hlsl" ), VERTEX_FORMAT.Pt4, "VS", null, "PS" );
				#endif


				// Create our constant buffers
				m_CB_Input = new RendererManaged.ConstantBuffer<CBInput>( m_Device, 0 );
				m_CB_Filter = new RendererManaged.ConstantBuffer<CBFilter>( m_Device, 0 );

				m_CB_Display = new ConstantBuffer<CBDisplay>( m_Device, 0 );
				m_CB_Display.m._Width = (uint) m_viewerForm.Width;
				m_CB_Display.m._Height = (uint) m_viewerForm.Height;

				// Create our structured buffer containing the rays
				m_SB_Rays = new RendererManaged.StructuredBuffer<RendererManaged.float3>( m_Device, 3*MAX_THREADS, true );
				integerTrackbarControlRaysCount_SliderDragStop( integerTrackbarControlRaysCount, 0 );

//				LoadHeightMap( new System.IO.FileInfo( "eye_generic_01_disp.png" ) );
//				LoadHeightMap( new System.IO.FileInfo( "10 - Smooth.jpg" ) );

			} catch ( Exception _e ) {
				MessageBox( "Failed to create DX11 device and default shaders:\r\n", _e );
				Close();
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e) {
			e.Cancel = false;
			base.OnFormClosing(e);
		}

		protected override void OnClosing( CancelEventArgs e ) {
			try {
				m_SB_Rays.Dispose();
				m_CB_Display.Dispose();
				m_CB_Filter.Dispose();
				m_CB_Input.Dispose();

				m_PS_Display.Dispose();
				m_CS_GenerateSSBumpMap.Dispose();
				m_CS_BilateralFilter.Dispose();

				if ( m_TextureTarget_CPU != null )
					m_TextureTarget_CPU.Dispose();
				if ( m_TextureTarget1 != null )
					m_TextureTarget1.Dispose();
				if ( m_TextureTarget0 != null )
					m_TextureTarget0.Dispose();
				if ( m_TextureSource != null )
					m_TextureSource.Dispose();

				m_Device.Dispose();
				m_Device = null;

			} catch ( Exception _e ) {
				MessageBox( "Failed to close:\r\n", _e );
			}

// FFS!!!
System.Diagnostics.Process	P = System.Diagnostics.Process.GetCurrentProcess();
P.Kill();

			e.Cancel = false;
			base.OnClosing( e );
		}

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose( bool disposing )
		{
			if ( disposing && (components != null) )
			{
				components.Dispose();
			}
			base.Dispose( disposing );
		}

		private void	LoadHeightMap( System.IO.FileInfo _FileName ) {
			try
			{
				tabControlGenerators.Enabled = false;

				// Dispose of existing resources
				if ( m_BitmapSource != null )
					m_BitmapSource.Dispose();
				m_BitmapSource = null;

				if ( m_TextureTarget_CPU != null )
					m_TextureTarget_CPU.Dispose();
				m_TextureTarget_CPU = null;
				if ( m_TextureTarget0 != null )
					m_TextureTarget0.Dispose();
				m_TextureTarget0 = null;
				if ( m_TextureTarget1 != null )
					m_TextureTarget1.Dispose();
				m_TextureTarget1 = null;
				if ( m_TextureSource != null )
					m_TextureSource.Dispose();
				m_TextureSource = null;

				// Load the source image assuming it's in linear space
				m_SourceFileName = _FileName;
				m_BitmapSource = new ImageUtility.Bitmap( _FileName, m_LinearProfile );
				outputPanelInputHeightMap.Image = m_BitmapSource;

				W = m_BitmapSource.Width;
				H = m_BitmapSource.Height;

				// Build the source texture
				RendererManaged.PixelsBuffer	SourceHeightMap = new RendererManaged.PixelsBuffer( W*H*4 );
				using ( System.IO.BinaryWriter Wr = SourceHeightMap.OpenStreamWrite() )
					for ( int Y=0; Y < H; Y++ )
						for ( int X=0; X < W; X++ )
							Wr.Write( m_BitmapSource.ContentXYZ[X,Y].y );

				m_TextureSource = new RendererManaged.Texture2D( m_Device, W, H, 1, 1, RendererManaged.PIXEL_FORMAT.R32_FLOAT, false, false, new RendererManaged.PixelsBuffer[] { SourceHeightMap } );

				// Build the target UAV & staging texture for readback
				m_TextureTarget0 = new RendererManaged.Texture2D( m_Device, W, H, 1, 1, RendererManaged.PIXEL_FORMAT.R32_FLOAT, false, true, null );
				m_TextureTarget1 = new RendererManaged.Texture2D( m_Device, W, H, 1, 1, RendererManaged.PIXEL_FORMAT.RGBA32_FLOAT, false, true, null );
				m_TextureTarget_CPU = new RendererManaged.Texture2D( m_Device, W, H, 1, 1, RendererManaged.PIXEL_FORMAT.RGBA32_FLOAT, true, false, null );

				tabControlGenerators.Enabled = true;
				buttonGenerate.Focus();
			}
			catch ( Exception _e )
			{
				MessageBox( "An error occurred while opening the image:\n\n", _e );
			}
		}

		private void	Generate() {
			try {
				tabControlGenerators.Enabled = false;

				//////////////////////////////////////////////////////////////////////////
				// 1] Apply bilateral filtering to the input texture as a pre-process
				ApplyBilateralFiltering( m_TextureSource, m_TextureTarget0, floatTrackbarControlBilateralRadius.Value, floatTrackbarControlBilateralTolerance.Value, checkBoxWrap.Checked );


				//////////////////////////////////////////////////////////////////////////
				// 2] Compute directional occlusion
				m_TextureTarget1.RemoveFromLastAssignedSlots();

				// Prepare computation parameters
				m_TextureTarget0.SetCS( 0 );
				m_TextureTarget1.SetCSUAV( 0 );
				m_SB_Rays.SetInput( 1 );

				m_CB_Input.m.RaysCount = (UInt32) Math.Min( MAX_THREADS, integerTrackbarControlRaysCount.Value );
				m_CB_Input.m.MaxStepsCount = (UInt32) integerTrackbarControlMaxStepsCount.Value;
				m_CB_Input.m.Tile = (uint) (checkBoxWrap.Checked ? 1 : 0);
				m_CB_Input.m.TexelSize_mm = TextureSize_mm / Math.Max( W, H );
				m_CB_Input.m.Displacement_mm = TextureHeight_mm;

				// Start
				if ( !m_CS_GenerateSSBumpMap.Use() )
					throw new Exception( "Can't generate self-shadowed bump map as compute shader failed to compile!" );

				int	h = Math.Max( 1, MAX_LINES*1024 / W );
				int	CallsCount = (int) Math.Ceiling( (float) H / h );
				for ( int i=0; i < CallsCount; i++ )
				{
					m_CB_Input.m.Y0 = (UInt32) (i * h);
					m_CB_Input.UpdateData();

					m_CS_GenerateSSBumpMap.Dispatch( W, h, 1 );

					m_Device.Present( true );

					progressBar.Value = (int) (0.01f * (BILATERAL_PROGRESS + (100-BILATERAL_PROGRESS) * (i+1) / (CallsCount)) * progressBar.Maximum);
//					for ( int a=0; a < 10; a++ )
						Application.DoEvents();
				}

				m_TextureTarget1.RemoveFromLastAssignedSlotUAV();	// So we can use it as input for next stage

				progressBar.Value = progressBar.Maximum;

				// Compute in a single shot (this is madness!)
// 				m_CB_Input.m.y = 0;
// 				m_CB_Input.UpdateData();
// 				m_CS_GenerateSSBumpMap.Dispatch( W, H, 1 );


				//////////////////////////////////////////////////////////////////////////
				// 3] Copy target to staging for CPU readback and update the resulting bitmap
				m_TextureTarget_CPU.CopyFrom( m_TextureTarget1 );

				if ( m_BitmapResult != null )
					m_BitmapResult.Dispose();
				m_BitmapResult = null;
				m_BitmapResult = new ImageUtility.Bitmap( W, H, m_LinearProfile );
				m_BitmapResult.HasAlpha = true;

				RendererManaged.PixelsBuffer	Pixels = m_TextureTarget_CPU.Map( 0, 0 );
				using ( System.IO.BinaryReader R = Pixels.OpenStreamRead() )
					for ( int Y=0; Y < H; Y++ )
					{
						R.BaseStream.Position = Y * Pixels.RowPitch;
						for ( int X=0; X < W; X++ )
						{
							ImageUtility.float4	Color = new ImageUtility.float4( R.ReadSingle(), R.ReadSingle(), R.ReadSingle(), R.ReadSingle() );
							Color = m_LinearProfile.RGB2XYZ( Color );
							m_BitmapResult.ContentXYZ[X,Y] = Color;
						}
					}

				Pixels.Dispose();
				m_TextureTarget_CPU.UnMap( 0, 0 );

				// Assign result
				viewportPanelResult.Image = m_BitmapResult;

			} catch ( Exception _e ) {
				MessageBox( "An error occurred during generation!\r\n\r\nDetails: ", _e );
			} finally {
				tabControlGenerators.Enabled = true;
			}
		}

		private void	ApplyBilateralFiltering( RendererManaged.Texture2D _Source, RendererManaged.Texture2D _Target, float _BilateralRadius, float _BilateralTolerance, bool _Wrap ) {
			_Source.SetCS( 0 );
			_Target.SetCSUAV( 0 );

			m_CB_Filter.m.Radius = _BilateralRadius;
			m_CB_Filter.m.Tolerance = _BilateralTolerance;
			m_CB_Filter.m.Tile = (uint) (_Wrap ? 1 : 0);

			m_CS_BilateralFilter.Use();

			int	h = Math.Max( 1, MAX_LINES*1024 / W );
			int	CallsCount = (int) Math.Ceiling( (float) H / h );
			for ( int i=0; i < CallsCount; i++ )
			{
				m_CB_Filter.m.Y0 = (UInt32) (i * h);
				m_CB_Filter.UpdateData();

				m_CS_BilateralFilter.Dispatch( W, h, 1 );

				m_Device.Present( true );

				progressBar.Value = (int) (0.01f * (0 + BILATERAL_PROGRESS * (i+1) / CallsCount) * progressBar.Maximum);
//				for ( int a=0; a < 10; a++ )
					Application.DoEvents();
			}

			// Single gulp (crashes the driver on large images!)
//			m_CS_BilateralFilter.Dispatch( W, H, 1 );

			_Target.RemoveFromLastAssignedSlotUAV();	// So we can use it as input for next stage
		}

		private void	GenerateRays( int _RaysCount, RendererManaged.StructuredBuffer<RendererManaged.float3> _Target ) {
			_RaysCount = Math.Min( MAX_THREADS, _RaysCount );

			// Half-Life 2 basis
			RendererManaged.float3[]	HL2Basis = new RendererManaged.float3[] {
				new RendererManaged.float3( (float) Math.Sqrt( 2.0 / 3.0 ), 0.0f, (float) Math.Sqrt( 1.0 / 3.0 ) ),
				new RendererManaged.float3( -(float) Math.Sqrt( 1.0 / 6.0 ), (float) Math.Sqrt( 1.0 / 2.0 ), (float) Math.Sqrt( 1.0 / 3.0 ) ),
				new RendererManaged.float3( -(float) Math.Sqrt( 1.0 / 6.0 ), -(float) Math.Sqrt( 1.0 / 2.0 ), (float) Math.Sqrt( 1.0 / 3.0 ) )
			};

			float	CenterTheta = (float) Math.Acos( HL2Basis[0].z );
			float[]	CenterPhi = new float[] {
				(float) Math.Atan2( HL2Basis[0].y, HL2Basis[0].x ),
				(float) Math.Atan2( HL2Basis[1].y, HL2Basis[1].x ),
				(float) Math.Atan2( HL2Basis[2].y, HL2Basis[2].x ),
			};

			for ( int RayIndex=0; RayIndex < _RaysCount; RayIndex++ ) {
				double	Phi = (Math.PI / 3.0) * (2.0 * WMath.SimpleRNG.GetUniform() - 1.0);

				// Stratified version
				double	Theta = (Math.Acos( Math.Sqrt( (RayIndex + WMath.SimpleRNG.GetUniform()) / _RaysCount ) ));

// 				// Don't give a shit version (a.k.a. melonhead version)
// //				double	Theta = Math.Acos( Math.Sqrt(WMath.SimpleRNG.GetUniform() ) );
// 				double	Theta = 0.5 * Math.PI * WMath.SimpleRNG.GetUniform();

				Theta = Math.Min( 0.499f * Math.PI, Theta );


				double	CosTheta = Math.Cos( Theta );
				double	SinTheta = Math.Sin( Theta );

				double	LengthFactor = 1.0 / SinTheta;	// The ray is scaled so we ensure we always walk at least a texel in the texture
				CosTheta *= LengthFactor;
				SinTheta *= LengthFactor;	// Yeah, yields 1... :)

				_Target.m[0*MAX_THREADS+RayIndex].Set(	(float) (Math.Cos( CenterPhi[0] + Phi ) * SinTheta),
														(float) (Math.Sin( CenterPhi[0] + Phi ) * SinTheta),
														(float) CosTheta );
				_Target.m[1*MAX_THREADS+RayIndex].Set(	(float) (Math.Cos( CenterPhi[1] + Phi ) * SinTheta),
														(float) (Math.Sin( CenterPhi[1] + Phi ) * SinTheta),
														(float) CosTheta );
				_Target.m[2*MAX_THREADS+RayIndex].Set(	(float) (Math.Cos( CenterPhi[2] + Phi ) * SinTheta),
														(float) (Math.Sin( CenterPhi[2] + Phi ) * SinTheta),
														(float) CosTheta );
			}

			_Target.Write();
		}

		#region Super Slow CPU Version (without bilateral)

		private void	Generate_CPU( int _RaysCount )
		{
			try
			{
				tabControlGenerators.Enabled = false;

				// Half-life basis (Z points outside of the surface, as in normal maps)
				WMath.Vector[]	Basis = new WMath.Vector[] {
					new WMath.Vector( (float) Math.Sqrt( 2.0 / 3.0 ), 0.0f, (float) Math.Sqrt( 1.0 / 3.0 ) ),
					new WMath.Vector( (float) -Math.Sqrt( 1.0 / 6.0 ), (float) Math.Sqrt( 1.0 / 2.0 ), (float) Math.Sqrt( 1.0 / 3.0 ) ),
					new WMath.Vector( (float) -Math.Sqrt( 1.0 / 6.0 ), (float) -Math.Sqrt( 1.0 / 2.0 ), (float) Math.Sqrt( 1.0 / 3.0 ) ),
				};

// 				// 1] Compute normal map
// 				WMath.Vector	dX = new WMath.Vector();
// 				WMath.Vector	dY = new WMath.Vector();
// 				WMath.Vector	N;
// 				float			ddX = floatTrackbarControlPixelSize.Value;
// 				float			ddH = floatTrackbarControlHeight.Value;
// 				for ( int Y=0; Y < H; Y++ )
// 				{
// 					int	Y0 = Math.Max( 0, Y-1 );
// 					int	Y1 = Math.Min( H-1, Y+1 );
// 					for ( int X=0; X < W; X++ )
// 					{
// 						int	X0 = Math.Max( 0, X-1 );
// 						int	X1 = Math.Min( W-1, X+1 );
// 
// 						float	Hx0 = m_BitmapSource.ContentXYZ[X0,Y].y;
// 						float	Hx1 = m_BitmapSource.ContentXYZ[X1,Y].y;
// 						float	Hy0 = m_BitmapSource.ContentXYZ[X,Y0].y;
// 						float	Hy1 = m_BitmapSource.ContentXYZ[X,Y1].y;
// 
// 						dX.Set( 2.0f * ddX, 0.0f, ddH * (Hx1 - Hx0) );
// 						dY.Set( 0.0f, 2.0f * ddX, ddH * (Hy1 - Hy0) );
// 
// 						N = dX.Cross( dY ).Normalized;
// 
// 						m_Normal[X,Y] = new WMath.Vector(
// 							N.Dot( Basis[0] ),
// 							N.Dot( Basis[1] ),
// 							N.Dot( Basis[2] ) );
// 					}
// 
// 					// Update and show progress
// 					UpdateProgress( m_Normal, Y, true );
// 				}
// 				UpdateProgress( m_Normal, H, true );

				float	LobeExponent = 4.0f;//floatTrackbarControlLobeExponent.Value;

				float	PixelSize_mm = 1000.0f / floatTrackbarControlPixelDensity.Value;

				float	Scale = 0.1f * PixelSize_mm / floatTrackbarControlHeight.Value;	// Scale factor to apply to pixel distances so they're renormalized in [0,1], our "heights space"...
//						Scale *= floatTrackbarControlZFactor.Value;	// Cheat Z velocity so AO is amplified!

				// 2] Build local rays only once
				int				RaysCount = integerTrackbarControlRaysCount.Value;
				WMath.Vector[,]	Rays = new WMath.Vector[3,RaysCount];

				// Create orthonormal bases to orient the lobe
				WMath.Vector	Xr = Basis[0].Cross( WMath.Vector.UnitZ ).Normalized;	// We can safely use (0,0,1) as the "up" direction since the HL2 basis doesn't have any vertical direction
				WMath.Vector	Yr = Xr.Cross( Basis[0] );
				WMath.Vector	Xg = Basis[1].Cross( WMath.Vector.UnitZ ).Normalized;	// We can safely use (0,0,1) as the "up" direction since the HL2 basis doesn't have any vertical direction
				WMath.Vector	Yg = Xg.Cross( Basis[1] );
				WMath.Vector	Xb = Basis[2].Cross( WMath.Vector.UnitZ ).Normalized;	// We can safely use (0,0,1) as the "up" direction since the HL2 basis doesn't have any vertical direction
				WMath.Vector	Yb = Xb.Cross( Basis[2] );

				double	Exponent = 1.0 / (1.0 + LobeExponent);
				for ( int RayIndex=0; RayIndex < RaysCount; RayIndex++ )
				{
// 					if ( false ) {
// 						double	Phi = 2.0 * Math.PI * WMath.SimpleRNG.GetUniform();
// //						double	Theta = Math.Acos( Math.Pow( WMath.SimpleRNG.GetUniform(), Exponent ) );
// 						double	Theta = Math.PI / 3.0 * WMath.SimpleRNG.GetUniform();
// 
// 						WMath.Vector	RayLocal = new WMath.Vector(
// 							(float) (Math.Cos( Phi ) * Math.Sin( Theta )),
// 							(float) (Math.Sin( Phi ) * Math.Sin( Theta )),
// 							(float) Math.Cos( Theta ) );
// 
// 						Rays[0,RayIndex] = RayLocal.x * Xr + RayLocal.y * Yr + RayLocal.z * Basis[0];
// 						Rays[1,RayIndex] = RayLocal.x * Xg + RayLocal.y * Yg + RayLocal.z * Basis[1];
// 						Rays[2,RayIndex] = RayLocal.x * Xb + RayLocal.y * Yb + RayLocal.z * Basis[2];
// 					}
// 					else
					{
						double	Phi = Math.PI / 3.0 * (2.0 * WMath.SimpleRNG.GetUniform() - 1.0);
						double	Theta = 0.49 * Math.PI * WMath.SimpleRNG.GetUniform();
						Rays[0,RayIndex] = new WMath.Vector(
							(float) (Math.Cos( Phi ) * Math.Sin( Theta )),
							(float) (Math.Sin( Phi ) * Math.Sin( Theta )),
							(float) Math.Cos( Theta ) );

						Phi = Math.PI / 3.0 * (2.0 * WMath.SimpleRNG.GetUniform() - 1.0 + 2.0);
						Theta = 0.49 * Math.PI * WMath.SimpleRNG.GetUniform();
						Rays[1,RayIndex] = new WMath.Vector(
							(float) (Math.Cos( Phi ) * Math.Sin( Theta )),
							(float) (Math.Sin( Phi ) * Math.Sin( Theta )),
							(float) Math.Cos( Theta ) );

						Phi = Math.PI / 3.0 * (2.0 * WMath.SimpleRNG.GetUniform() - 1.0 + 4.0);
						Theta = 0.49 * Math.PI * WMath.SimpleRNG.GetUniform();
						Rays[2,RayIndex] = new WMath.Vector(
							(float) (Math.Cos( Phi ) * Math.Sin( Theta )),
							(float) (Math.Sin( Phi ) * Math.Sin( Theta )),
							(float) Math.Cos( Theta ) );
					}

					Rays[0,RayIndex].z *= Scale;
					Rays[1,RayIndex].z *= Scale;
					Rays[2,RayIndex].z *= Scale;
				}

				// 3] Compute directional occlusion
				for ( int Y=0; Y < H; Y++ )
				{
					for ( int X=0; X < W; X++ )
					{
						float	R = ComputeAO( 0, X, Y, Scale, Rays );
						float	G = ComputeAO( 1, X, Y, Scale, Rays );
						float	B = ComputeAO( 2, X, Y, Scale, Rays );
//						N = m_Normal[X,Y];
// 
 						m_BitmapResult.ContentXYZ[X,Y] = m_LinearProfile.RGB2XYZ( new ImageUtility.float4( R, G, B, (R+G+B)/3.0f ) );
					}

					// Update and show progress
					UpdateProgress( m_BitmapResult, Y, true );
				}
				UpdateProgress( m_BitmapResult, H, true );

//				m_BitmapResult.Save( "eye_generic_01_disp_hl2.png", ImageFormat.Png );
			}
			catch ( Exception _e )
			{
				MessageBox( "An error occurred during generation:\r\n" + _e.Message, MessageBoxButtons.OK, MessageBoxIcon.Error );
			}
			finally
			{
				tabControlGenerators.Enabled = true;
			}
		}

		/// <summary>
		/// Computes the ambient occlusion of the specified coordinate by shooting N rays in a lobe oriented in the specified light direction
		/// </summary>
		/// <param name="_Light"></param>
		/// <param name="_X"></param>
		/// <param name="_Y"></param>
		/// <param name="_Z2HeightScale">Scale factor to apply to world Z coordinate to be remapped into the heights' [0,1] range</param>
		/// <param name="_LobeExponent">1 is a simple cosine lobe</param>
		/// <returns></returns>
		/// 
		private float	ComputeAO( int _LightIndex, int _X, int _Y, float _Z2HeightScale, WMath.Vector[,] _Rays )
		{
			int		RaysCount = _Rays.GetLength( 1 );
			int		MaxStepsCount = integerTrackbarControlMaxStepsCount.Value;

			float	Z0 = m_BitmapSource.ContentXYZ[_X,_Y].y;
			double	AO = 0.0f;
			int		SamplesCount = 0;
			for ( int RayIndex=0; RayIndex < RaysCount; RayIndex++ )
			{
				WMath.Vector	RayWorld = _Rays[_LightIndex,RayIndex];
 				if ( RayWorld.z < 0.0f )
				{
// AO += 1.0;
// SamplesCount++;
 					continue;	// Pointing to the ground so don't account for it...
				}

				// Make sure the ray has a unit step so we always travel at least one pixel
//				m_RayWorld.z *= _Z2HeightScale;
// 				float	Normalizer = 1.0f / Math.Max( Math.Abs( m_RayWorld.x ), Math.Abs( m_RayWorld.y ) );
// 				float	Normalizer = 1.0f;
// 				Normalizer = Math.Max( Normalizer, (1.0f - Z) / (128.0f * m_RayWorld.z) );	// This makes sure we can't use more than 128 steps to escape the heightfield
// 
// 				float	Normalizer = (1.0f - Z) / (128.0f * m_RayWorld.z);
// 
// 				m_RayWorld.x *= Normalizer;
// 				m_RayWorld.y *= Normalizer;
// 				m_RayWorld.z *= Normalizer;

				// Start from the provided coordinates
				float	X = _X;
				float	Y = _Y;
				float	Z = Z0;

				// Compute intersection with the height field
				int	StepIndex = 0;
				while ( StepIndex < MaxStepsCount && Z < 1.0f && X > 0.0f && Y > 0.0f && X < W && Y < H )
				{
					X += RayWorld.x;
					Y += RayWorld.y;
					Z += RayWorld.z;

					float	Height = SampleHeightField( X, Y );
					if ( Height > Z )
					{	// Hit!
						AO += 1.0;
						break;
					}

					StepIndex++;
				}

				SamplesCount++;
			}
			AO /= SamplesCount;

			return (float) (1.0 - AO);
		}

		private float	SampleHeightField( float _X, float _Y )
		{
// 			_X *= W;
// 			_Y *= H;
			int		X0 = (int) Math.Floor( _X );
			int		Y0 = (int) Math.Floor( _Y );
			float	x = _X - X0;
			float	y = _Y - Y0;
			X0 = Math.Max( 0, Math.Min( W-1, X0 ) );
			Y0 = Math.Max( 0, Math.Min( H-1, Y0 ) );
			int		X1 = Math.Min( W-1, X0+1 );
			int		Y1 = Math.Min( H-1, Y0+1 );

			float	V00 = m_BitmapSource.ContentXYZ[X0,Y0].y;
			float	V01 = m_BitmapSource.ContentXYZ[X1,Y0].y;
			float	V10 = m_BitmapSource.ContentXYZ[X0,Y1].y;
			float	V11 = m_BitmapSource.ContentXYZ[X1,Y1].y;

			float	V0 = V00 + (V01-V00) * x;
			float	V1 = V10 + (V11-V10) * x;

			float	V = V0 + (V1-V0) * y;
			return V;
		}

		private unsafe void	UpdateProgress( ImageUtility.Bitmap _Image, int Y, bool _Bias )
		{
			const int	REFRESH_EVERY_N_SCANLINES = 4;

			if ( Y == 0 || (Y & (REFRESH_EVERY_N_SCANLINES-1)) != 0 )
				return;

			viewportPanelResult.Image = _Image;
			Application.DoEvents();
		}

		#endregion

		#region Helpers

		private string	GetRegKey( string _Key, string _Default )
		{
			string	Result = m_AppKey.GetValue( _Key ) as string;
			return Result != null ? Result : _Default;
		}
		private void	SetRegKey( string _Key, string _Value )
		{
			m_AppKey.SetValue( _Key, _Value );
		}

		private float	GetRegKeyFloat( string _Key, float _Default )
		{
			string	Value = GetRegKey( _Key, _Default.ToString() );
			float	Result;
			float.TryParse( Value, out Result );
			return Result;
		}

		private int		GetRegKeyInt( string _Key, float _Default )
		{
			string	Value = GetRegKey( _Key, _Default.ToString() );
			int		Result;
			int.TryParse( Value, out Result );
			return Result;
		}

		public DialogResult	MessageBox( string _Text )
		{
			return MessageBox( _Text, MessageBoxButtons.OK );
		}
		private DialogResult	MessageBox( string _Text, Exception _e )
		{
			return MessageBox( _Text + _e.Message, MessageBoxButtons.OK, MessageBoxIcon.Error );
		}
		private DialogResult	MessageBox( string _Text, MessageBoxButtons _Buttons )
		{
			return MessageBox( _Text, _Buttons, MessageBoxIcon.Information );
		}
		private DialogResult	MessageBox( string _Text, MessageBoxIcon _Icon )
		{
			return MessageBox( _Text, MessageBoxButtons.OK, _Icon );
		}
		private DialogResult	MessageBox( string _Text, MessageBoxButtons _Buttons, MessageBoxIcon _Icon )
		{
			return System.Windows.Forms.MessageBox.Show( this, _Text, "SSBumpMap Generator", _Buttons, _Icon );
		}

		#endregion 

		#endregion

		#region EVENT HANDLERS

 		private unsafe void buttonGenerate_Click( object sender, EventArgs e )
 		{
 			Generate();
//			Generate_CPU( integerTrackbarControlRaysCount.Value );
		}

		private void integerTrackbarControlRaysCount_SliderDragStop( Nuaj.Cirrus.Utility.IntegerTrackbarControl _Sender, int _StartValue )
		{
			GenerateRays( _Sender.Value, m_SB_Rays );
		}

		private void radioButtonShowDirOccRGB_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.RGB;
		}

		private void radioButtonDirOccRGBtimeAO_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.RGB_AO;
		}

		private void radioButtonDirOccR_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.R;
		}

		private void radioButtonDirOccG_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.G;
		}

		private void radioButtonDirOccB_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.B;
		}

		private void radioButton1_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.AO;
		}

		private void radioButtonAOfromRGB_CheckedChanged( object sender, EventArgs e )
		{
			if ( (sender as RadioButton).Checked )
				viewportPanelResult.ViewMode = ImagePanel.VIEW_MODE.AO_FROM_RGB;
		}

		private unsafe void viewportPanelResult_Click( object sender, EventArgs e )
		{
			if ( m_BitmapResult == null )
			{
				MessageBox( "There is no result image to save!" );
				return;
			}

			string	SourceFileName = m_SourceFileName.FullName;
			string	TargetFileName = System.IO.Path.Combine( System.IO.Path.GetDirectoryName( SourceFileName ), System.IO.Path.GetFileNameWithoutExtension( SourceFileName ) + "_ssbump.png" );

			saveFileDialogImage.InitialDirectory = System.IO.Path.GetFullPath( TargetFileName );
			saveFileDialogImage.FileName = System.IO.Path.GetFileName( TargetFileName );
			if ( saveFileDialogImage.ShowDialog( this ) != DialogResult.OK )
				return;

			try
			{
				m_BitmapResult.Save( new System.IO.FileInfo( saveFileDialogImage.FileName ) );

				MessageBox( "Success!", MessageBoxButtons.OK, MessageBoxIcon.Information );
			}
			catch ( Exception _e )
			{
				MessageBox( "An error occurred while saving the image:\n\n", _e );
			}
		}

		private void checkBoxShowsRGB_CheckedChanged( object sender, EventArgs e )
		{
			outputPanelInputHeightMap.ViewLinear = !checkBoxShowsRGB.Checked;
			viewportPanelResult.ViewLinear = !checkBoxShowsRGB.Checked;
		}

		private void outputPanelInputHeightMap_Click( object sender, EventArgs e )
		{
			string	OldFileName = GetRegKey( "DatabaseFileName", System.IO.Path.Combine( m_ApplicationPath, "Example.jpg" ) );
			openFileDialogImage.InitialDirectory = System.IO.Path.GetFullPath( OldFileName );
			openFileDialogImage.FileName = System.IO.Path.GetFileName( OldFileName );
			if ( openFileDialogImage.ShowDialog( this ) != DialogResult.OK )
				return;

			SetRegKey( "DatabaseFileName", openFileDialogImage.FileName );

			LoadHeightMap( new System.IO.FileInfo( openFileDialogImage.FileName ) );
		}

		private string	m_DraggedFileName = null;
		private void outputPanelInputHeightMap_DragEnter( object sender, DragEventArgs e )
		{
			m_DraggedFileName = null;
			if ( (e.AllowedEffect & DragDropEffects.Copy) != DragDropEffects.Copy )
				return;

			Array	data = ((IDataObject) e.Data).GetData( "FileNameW" ) as Array;
			if ( data == null || data.Length != 1 )
				return;
			if ( !(data.GetValue(0) is String) )
				return;

			string	DraggedFileName = (data as string[])[0];

			string	Extension = System.IO.Path.GetExtension( DraggedFileName ).ToLower();
			if (	Extension == ".jpg"
				||	Extension == ".jpeg"
				||	Extension == ".png"
				||	Extension == ".tga"
				||	Extension == ".bmp"
				||	Extension == ".tif"
				||	Extension == ".tiff"
				||	Extension == ".hdr"
				||	Extension == ".crw"
				||	Extension == ".dng"
				)
			{
				m_DraggedFileName = DraggedFileName;	// Supported!
				e.Effect = DragDropEffects.Copy;
			}
		}

		private void outputPanelInputHeightMap_DragDrop( object sender, DragEventArgs e )
		{
			if ( m_DraggedFileName != null )
				LoadHeightMap( new System.IO.FileInfo( m_DraggedFileName ) );
		}

		private void buttonReload_Click( object sender, EventArgs e )
		{
			m_Device.ReloadModifiedShaders();
		}

		private void buttonTest_Click( object sender, EventArgs e )
		{
			if ( m_viewerForm.Visible )
				m_viewerForm.Hide();
			else
				m_viewerForm.Show( this );
		}

		#endregion
	}
}
