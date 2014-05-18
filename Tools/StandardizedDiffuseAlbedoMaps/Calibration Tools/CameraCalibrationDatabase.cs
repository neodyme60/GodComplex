﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

using WMath;

namespace StandardizedDiffuseAlbedoMaps
{
	/// <summary>
	/// This class hosts the camera calibration database
	/// </summary>
	public class CameraCalibrationDatabase
	{
		#region CONSTANTS

		private const int	REQUIRED_PROBES_COUNT = 6;	// We're expecting 6 probes in the camera calibration files

		#endregion

		#region NESTED TYPES

		private class	GridNode
		{
			public CameraCalibration	m_CameraCalibration = null;

			public float				m_EV_ISOSpeed;
			public float				m_EV_ShutterSpeed;
			public float				m_EV_Aperture;

			// The 6 possible neighbors for this node
			public GridNode[]			m_NeighborX = new GridNode[2];
			public GridNode[]			m_NeighborY = new GridNode[2];
			public GridNode[]			m_NeighborZ = new GridNode[2];

			/// <summary>
			/// Gets the global EV for this node
			/// </summary>
			public float	EV	{ get { return m_EV_ISOSpeed + m_EV_ShutterSpeed + m_EV_Aperture; } }

			public	GridNode( CameraCalibration _CameraCalibration )
			{
				if ( _CameraCalibration == null )
					throw new Exception( "Invalid camera calibration to build grid node!" );

				m_CameraCalibration = _CameraCalibration;

				// Build normalized EV infos
				Convert2EV( m_CameraCalibration.m_CameraShotInfos.m_ISOSpeed,
							m_CameraCalibration.m_CameraShotInfos.m_ShutterSpeed,
							m_CameraCalibration.m_CameraShotInfos.m_Aperture,
							out m_EV_ISOSpeed, out m_EV_ShutterSpeed, out m_EV_Aperture );
			}

			/// <summary>
			/// Computes the square distance of the current node and provided node
			/// </summary>
			/// <param name="_Node"></param>
			/// <returns></returns>
			public float	SqDistance( GridNode _Node )
			{
				float	Delta_ISOSpeed = _Node.m_EV_ISOSpeed - m_EV_ISOSpeed;
				float	Delta_ShutterSpeed = _Node.m_EV_ShutterSpeed - m_EV_ShutterSpeed;
				float	Delta_Aperture = _Node.m_EV_Aperture - m_EV_Aperture;
				return Delta_ISOSpeed*Delta_ISOSpeed + Delta_ShutterSpeed*Delta_ShutterSpeed + Delta_Aperture*Delta_Aperture;
			}

			/// <summary>
			/// Converts to normalized EV infos
			/// </summary>
			/// <param name="_ISOSpeed"></param>
			/// <param name="_ShutterSpeed"></param>
			/// <param name="_Aperture"></param>
			/// <param name="_EV_ISOSpeed"></param>
			/// <param name="_EV_ShutterSpeed"></param>
			/// <param name="_EV_Aperture"></param>
			public static void	Convert2EV( float _ISOSpeed, float _ShutterSpeed, float _Aperture, out float _EV_ISOSpeed, out float _EV_ShutterSpeed, out float _EV_Aperture )
			{
				_EV_ISOSpeed = (float) (Math.Log( _ISOSpeed / 100.0f ) / Math.Log(2.0));	// 100 ISO = 0 EV, 200 = +1 EV, 400 = +2 EV, etc.
				_EV_ShutterSpeed = (float) (Math.Log( _ShutterSpeed ) / Math.Log(2.0));		// 1s = 0 EV, 2s = +1 EV, 0.5s = -1 EV, etc.
				_EV_Aperture = (float) (-Math.Log( _Aperture ) / Math.Log(2.0));			// f/1.0 = 0 EV, f/1.4 = -0.5 EV, f/2.0 = -1 EV, etc.
			}
		}

		#endregion

		#region FIELDS

		private System.IO.DirectoryInfo		m_DatabasePath = null;
		private string						m_ErrorLog = "";

		// The list of camera calibration data contained in the database
		private CameraCalibration[]			m_CameraClibrations = new CameraCalibration[0];

		// Generated calibration grid
		private GridNode					m_RootNode = null;

		// Cached calibration data
		private float						m_PreparedForISOSpeed = 0.0f;
		private float						m_PreparedForShutterSpeed = 0.0f;
		private float						m_PreparedForAperture = 0.0f;
		private CameraCalibration			m_InterpolatedCalibration = null;

		#endregion

		#region PROPERTIES

		public System.IO.DirectoryInfo		DatabasePath
		{
			get { return m_DatabasePath; }
			set
			{
				if ( m_DatabasePath != null )
				{	// Clean up existing database
					m_CameraClibrations = new CameraCalibration[0];
					m_PreparedForISOSpeed = 0.0f;
					m_PreparedForShutterSpeed = 0.0f;
					m_PreparedForAperture = 0.0f;
					m_InterpolatedCalibration = null;
					m_RootNode = null;
					m_ErrorLog = "";
				}

				m_DatabasePath = value;

				if ( m_DatabasePath == null )
					return;

				// Setup new database
				if ( !m_DatabasePath.Exists )
					throw new Exception( "Provided database path doesn't exist!" );

				//////////////////////////////////////////////////////////////////////////
				// Collect all calibration files
				System.IO.FileInfo[]	CalibrationFiles = m_DatabasePath.GetFiles( "*.xml" );
				List<CameraCalibration>	CameraCalibrations = new List<CameraCalibration>();
				List<GridNode>			GridNodes = new List<GridNode>();
				foreach ( System.IO.FileInfo CalibrationFile in CalibrationFiles )
				{
					try
					{
						// Attempt to load the camera calibration file
						CameraCalibration	CC = new CameraCalibration();
						CC.Load( CalibrationFile );
						if ( CC.m_Reflectances.Length != REQUIRED_PROBES_COUNT )
							throw new Exception( "Unexpected amount of reflectance probes in calibration file! (" + REQUIRED_PROBES_COUNT + " required)" );

						// Attempt to create a valid grid node from it
						GridNode	Node = new GridNode( CC );
						if ( m_RootNode == null || Node.EV < m_RootNode.EV )
							m_RootNode = Node;	// Found a better root

						// If everything went well, add the new data
						CameraCalibrations.Add( CC );
						GridNodes.Add( Node );
					}
					catch ( Exception _e )
					{
						m_ErrorLog += "Failed to load camera calibration file \"" + CalibrationFile.FullName + "\": " + _e.Message + "\r\n";
					}
				}
				m_CameraClibrations = CameraCalibrations.ToArray();

				if ( m_CameraClibrations.Length == 0 )
				{	// Empty!
					m_ErrorLog += "Database is empty: no valid file could be parsed...\r\n";
					return;
				}

				//////////////////////////////////////////////////////////////////////////
				// Build the calibration grid
				// The idea is to build a 3D grid of camera calibration settings, each dimension of the grid is:
				//	_ X = ISO Speed
				//	_ Y = Shutter Speed
				//	_ Z = Aperture
				//
				// Each grid node has neighbors to the previous/next EV value along the specific dimension.
				// For example, following the "next" X neighbor, you will increase the EV on the ISO speed parameter.
				// For example, following the "previous" Y neighbor, you will decrease the EV on the Shutter speed parameter.
				// For example, following the "next" Z neighbor, you will increase the EV on the Aperture parameter.
				//
				// You should imagine the grid as a 3D texture, except the voxels of the texture are not regularly spaced
				//	but can be placed freely in the volume. The grid only maintains the coherence from one voxel to another.
				//
				List<GridNode>	PlacedNodes = new List<GridNode>();
				PlacedNodes.Add( m_RootNode );
				GridNodes.Remove( m_RootNode );

				while ( GridNodes.Count > 0 )
				{
					// The algorithm is simple:
					//	_ While there are still unplaced nodes
					//		_ For each pair of (placed,unplaced) grid nodes
					//			_ If the pair is closer than current pair, then make it the new current pair
					//		_ If the largest EV discrepancy is ISO speed then store unplaced node on X axis (either previous or next depending on sign of discrepancy)
					//		_ If the largest EV discrepancy is shutter speed then store unplaced node on Y axis
					//		_ If the largest EV discrepancy is aperture then store unplaced node on Z axis (either previous or next depending on sign of discrepancy)
					//
					GridNode	PairPlaced = null;
					GridNode	PairUnPlaced = null;
					float		BestPairSqDistance = float.MaxValue;
					foreach ( GridNode NodePlaced in PlacedNodes )
						foreach ( GridNode NodeUnPlaced in GridNodes )
						{
							float	SqDistance = NodePlaced.SqDistance( NodeUnPlaced );
							if ( SqDistance < BestPairSqDistance )
							{	// Found new best pair!
								BestPairSqDistance = SqDistance;
								PairPlaced = NodePlaced;
								PairUnPlaced = NodeUnPlaced;
							}
						}

					// So now we know a new neighbor for the placed node
					// We need to know on which axis and which direction...
					float	DeltaX = PairPlaced.m_EV_ISOSpeed - PairUnPlaced.m_EV_ISOSpeed;
					float	DeltaY = PairPlaced.m_EV_ShutterSpeed - PairUnPlaced.m_EV_ShutterSpeed;
					float	DeltaZ = PairPlaced.m_EV_Aperture - PairUnPlaced.m_EV_Aperture;

					GridNode[]	AxisPlaced = null;
					GridNode[]	AxisUnPlaced = null;
					int			LeftRight = -1;
					if ( Math.Abs( DeltaX ) > Math.Abs( DeltaY ) )
					{
						if ( Math.Abs( DeltaX ) > Math.Abs( DeltaZ ) )
						{	// Place along X
							AxisPlaced = PairPlaced.m_NeighborX;
							AxisUnPlaced = PairUnPlaced.m_NeighborX;
							LeftRight = DeltaX < 0.0f ? 0 : 1;
						}
						else
						{	// Place along Z
							AxisPlaced = PairPlaced.m_NeighborZ;
							AxisUnPlaced = PairUnPlaced.m_NeighborZ;
							LeftRight = DeltaZ < 0.0f ? 0 : 1;
						}
					}
					else
					{
						if ( Math.Abs( DeltaY ) > Math.Abs( DeltaZ ) )
						{	// Place along Y
							AxisPlaced = PairPlaced.m_NeighborY;
							AxisUnPlaced = PairUnPlaced.m_NeighborY;
							LeftRight = DeltaY < 0.0f ? 0 : 1;
						}
						else
						{	// Place along Z
							AxisPlaced = PairPlaced.m_NeighborZ;
							AxisUnPlaced = PairUnPlaced.m_NeighborZ;
							LeftRight = DeltaZ < 0.0f ? 0 : 1;
						}
					}

					// Store the neighbors for each node of the pair
					if ( AxisPlaced[LeftRight] != null )
						throw new Exception( "Grid node already has a neighbor!" );	// How can that be?
					AxisPlaced[LeftRight] = PairUnPlaced;
					AxisUnPlaced[1-LeftRight] = PairPlaced;

					// Remove the node from unplaced nodes
					GridNodes.Remove( PairUnPlaced );
				}
			}
		}

		/// <summary>
		/// Tells if the database is valid
		/// </summary>
		public bool		IsValid					{ get { return m_RootNode != null; } }

		/// <summary>
		/// Tells if there were some errors during construction
		/// </summary>
		public bool		HasErrors				{ get { return m_ErrorLog != ""; } }

		/// <summary>
		/// Shows error during database construction if not ""
		/// </summary>
		public string	ErrorLog				{ get { return m_ErrorLog; } }

		public float	PreparedForISOSpeed		{ get { return m_PreparedForISOSpeed; } }
		public float	PreparedForShutterSpeed	{ get { return m_PreparedForShutterSpeed; } }
		public float	PreparedForAperture		{ get { return m_PreparedForAperture; } }

		#endregion

		#region METHODS

		/// <summary>
		/// Prepares the 8 closest calibration tables to process the pixels in an image shot with the specified shot infos
		/// </summary>
		/// <param name="_ISOSpeed"></param>
		/// <param name="_ShutterSpeed"></param>
		/// <param name="_Aperture"></param>
		public void	PrepareCalibrationFor( float _ISOSpeed, float _ShutterSpeed, float _Aperture )
		{
			if ( m_RootNode == null )
				throw new Exception( "Calibration grid hasn't been built: did you provide a valid database path? Does the path contain camera calibration data?" );

			m_PreparedForISOSpeed = _ISOSpeed;
			m_PreparedForShutterSpeed = _ShutterSpeed;
			m_PreparedForAperture = _Aperture;

			//////////////////////////////////////////////////////////////////////////
			// Find the 8 nodes encompassing our values
			// I'm making the delicate assumption that, although the starting node is chosen on the
			//	condition it's EV values are strictly inferior to the target we're looking for, all
			//	neighbor nodes will satisfy the condition they're properly placed.
			//
			// This is true for the direct neighbors +X, +Y, +Z that are immediately above target values
			//	but for example, neighbor (+X +Y) may have a very bad aperture value (Z) that may be
			//	above the target aperture...
			//
			// Let's hope the user won't provide too fancy calibrations...
			// (anyway, interpolants are clamped in [0,1] so there's no risk of overshooting)
			//
			float3	EV;
			GridNode.Convert2EV( _ISOSpeed, _ShutterSpeed, _Aperture, out EV.x, out EV.y, out EV.z );

			// Find the start node
			GridNode		StartNode = FindStartNode( _ISOSpeed, _ShutterSpeed, _Aperture );

			// Build the 8 grid nodes from it
			GridNode[,,]	Grid = new GridNode[2,2,2];
			Grid[0,0,0] = StartNode;
			Grid[1,0,0] = StartNode.m_NeighborX[1] != null ? StartNode.m_NeighborX[1] : StartNode;			// +X
			Grid[0,1,0] = StartNode.m_NeighborY[1] != null ? StartNode.m_NeighborY[1] : StartNode;			// +Y
			Grid[0,0,1] = StartNode.m_NeighborZ[1] != null ? StartNode.m_NeighborZ[1] : StartNode;			// +Z
			Grid[1,1,0] = Grid[1,0,0].m_NeighborY[1] != null ? Grid[1,0,0].m_NeighborY[1] : Grid[1,0,0];	// +X +Y
			Grid[0,1,1] = Grid[0,1,0].m_NeighborZ[1] != null ? Grid[0,1,0].m_NeighborZ[1] : Grid[0,1,0];	// +Y +Z
			Grid[1,0,1] = Grid[0,0,1].m_NeighborX[1] != null ? Grid[0,0,1].m_NeighborX[1] : Grid[0,0,1];	// +X +Z
			Grid[1,1,1] = Grid[1,1,0].m_NeighborZ[1] != null ? Grid[1,1,0].m_NeighborZ[1] : Grid[1,1,0];	// +X +Y +Z

			//////////////////////////////////////////////////////////////////////////
			// Create the successive interpolants for trilinear interpolation
			//
			// Assume we interpolate on X first (ISO speed), so we need 4 distinct values
			float4	tX = new float4(
					Math.Max( 0.0f, Math.Min( 1.0f, (EV.x - Grid[0,0,0].m_EV_ISOSpeed) / Math.Max( 1e-6f, Grid[1,0,0].m_EV_ISOSpeed - Grid[0,0,0].m_EV_ISOSpeed) ) ),
					Math.Max( 0.0f, Math.Min( 1.0f, (EV.x - Grid[0,1,0].m_EV_ISOSpeed) / Math.Max( 1e-6f, Grid[1,1,0].m_EV_ISOSpeed - Grid[0,1,0].m_EV_ISOSpeed) ) ),
					Math.Max( 0.0f, Math.Min( 1.0f, (EV.x - Grid[0,0,1].m_EV_ISOSpeed) / Math.Max( 1e-6f, Grid[1,0,1].m_EV_ISOSpeed - Grid[0,0,1].m_EV_ISOSpeed) ) ),
					Math.Max( 0.0f, Math.Min( 1.0f, (EV.x - Grid[0,1,1].m_EV_ISOSpeed) / Math.Max( 1e-6f, Grid[1,1,1].m_EV_ISOSpeed - Grid[0,1,1].m_EV_ISOSpeed) ) )
				);
			float4	rX = new float4( 1.0f - tX.x, 1.0f - tX.y, 1.0f - tX.z, 1.0f - tX.w );

				// Compute the 4 interpolated shutter speeds & apertures
			float4	ShutterSpeedsX = new float4(
					rX.x * Grid[0,0,0].m_EV_ShutterSpeed + tX.x * Grid[1,0,0].m_EV_ShutterSpeed,	// Y=0 Z=0
					rX.y * Grid[0,1,0].m_EV_ShutterSpeed + tX.y * Grid[1,1,0].m_EV_ShutterSpeed,	// Y=1 Z=0
					rX.z * Grid[0,0,1].m_EV_ShutterSpeed + tX.z * Grid[1,0,1].m_EV_ShutterSpeed,	// Y=0 Z=1
					rX.w * Grid[0,1,1].m_EV_ShutterSpeed + tX.w * Grid[1,1,1].m_EV_ShutterSpeed		// Y=1 Z=1
				);
			float4	AperturesX = new float4(
					rX.x * Grid[0,0,0].m_EV_Aperture + tX.x * Grid[1,0,0].m_EV_Aperture,
					rX.y * Grid[0,1,0].m_EV_Aperture + tX.y * Grid[1,1,0].m_EV_Aperture,
					rX.z * Grid[0,0,1].m_EV_Aperture + tX.z * Grid[1,0,1].m_EV_Aperture,
					rX.w * Grid[0,1,1].m_EV_Aperture + tX.w * Grid[1,1,1].m_EV_Aperture
				);

			// Next we interpolate on Y (Shutter speed), so we need 2 distinct values
			float2	tY = new float2(
					Math.Max( 0.0f, Math.Min( 1.0f, (EV.y - ShutterSpeedsX.x) / Math.Max( 1e-6f, ShutterSpeedsX.y - ShutterSpeedsX.x) ) ),	// Z=0
					Math.Max( 0.0f, Math.Min( 1.0f, (EV.y - ShutterSpeedsX.z) / Math.Max( 1e-6f, ShutterSpeedsX.w - ShutterSpeedsX.z) ) )	// Z=1
				);
			float2	rY = new float2( 1.0f - tY.x, 1.0f - tY.y );
				// Compute the 2 apertures
			float2	AperturesY = new float2(
					rY.x * AperturesX.x + tY.x * AperturesX.y,
					rY.y * AperturesX.z + tY.y * AperturesX.w
				);

			// Finally, we interpolate on Z (Aperture), we need only 1 single value
			float	tZ = Math.Max( 0.0f, Math.Min( 1.0f, (EV.z - AperturesY.x) / Math.Max( 1e-6f, AperturesY.y - AperturesY.x) ) );
			float	rZ = 1.0f - tZ;

			//////////////////////////////////////////////////////////////////////////
			// Create the special camera calibration that is the result of the interpolation of the 8 ones in the grid
			m_InterpolatedCalibration = new CameraCalibration();

			for ( int ProbeIndex=0; ProbeIndex < REQUIRED_PROBES_COUNT; ProbeIndex++ )
			{
				CameraCalibration.Probe TargetProbe = m_InterpolatedCalibration.m_Reflectances[ProbeIndex];

				float	L000 = Grid[0,0,0].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L100 = Grid[1,0,0].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L010 = Grid[0,1,0].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L110 = Grid[1,1,0].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L001 = Grid[0,0,1].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L101 = Grid[1,0,1].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L011 = Grid[0,1,1].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;
				float	L111 = Grid[1,1,1].m_CameraCalibration.m_Reflectances[ProbeIndex].m_LuminanceMeasured;

				// Interpolate on X (ISO speed)
				float	L00 = rX.x * L000 + tX.x * L100;
				float	L10 = rX.x * L010 + tX.x * L110;
				float	L01 = rX.x * L001 + tX.x * L101;
				float	L11 = rX.x * L011 + tX.x * L111;

				// Interpolate on Y (shutter speed)
				float	L0 = rY.x * L00 + tY.x * L10;
				float	L1 = rY.y * L01 + tY.y * L11;

				// Interpolate on Z (aperture)
				float	L = rZ * L0 + tZ * L1;

				TargetProbe.m_LuminanceMeasured = L;
			}
		}

		/// <summary>
		/// Tells if the database is prepared and can be used for processing colors of an image with the specified shot infos
		/// </summary>
		/// <param name="_ISOSpeed"></param>
		/// <param name="_ShutterSpeed"></param>
		/// <param name="_Aperture"></param>
		/// <returns></returns>
		public bool	IsPreparedFor( float _ISOSpeed, float _ShutterSpeed, float _Aperture )
		{
			return Math.Abs( _ISOSpeed - m_PreparedForISOSpeed ) < 1e-6f
				&& Math.Abs( _ShutterSpeed - m_PreparedForShutterSpeed ) < 1e-6f
				&& Math.Abs( _Aperture - m_PreparedForAperture ) < 1e-6f;
		}

		/// <summary>
		/// Calibrates a raw luminance value
		/// </summary>
		/// <param name="_Luminance">The uncalibrated luminance value</param>
		/// <returns>The calibrated luminance value</returns>
		/// <remarks>Typically, you start from a RAW XYZ value that you convert to xyY, pass the Y to this method
		/// and replace it into your orignal xyY, convert back to XYZ and voilà!</remarks>
		public float	Calibrate( float _Luminance )
		{
			if ( m_RootNode == null )
				throw new Exception( "Calibration grid hasn't been built: did you provide a valid database path? Does the path contain camera calibration data?" );
			if ( m_InterpolatedCalibration == null )
				throw new Exception( "Calibration grid hasn't been prepared for calibration: did you call the PrepareCalibrationFor() method?" );
			
			_Luminance = m_InterpolatedCalibration.Calibrate( _Luminance );

			return _Luminance;
		}

		/// <summary>
		/// Finds the first grid node whose ISO, shutter speed and aperture values are immediately inferior to the ones provided
		/// </summary>
		/// <param name="_ISOSpeed"></param>
		/// <param name="_ShutterSpeed"></param>
		/// <param name="_Aperture"></param>
		/// <returns></returns>
		private GridNode	FindStartNode( float _ISOSpeed, float _ShutterSpeed, float _Aperture )
		{
			GridNode	Current = m_RootNode;

			// Move along X
			while ( Current.m_EV_ISOSpeed <= _ISOSpeed && Current.m_NeighborX[1] != null )
			{
				GridNode	Next = Current.m_NeighborX[1];
				if ( Next.m_EV_ISOSpeed > _ISOSpeed )
					break;	// Next node is larger than provided value! We have our start node along X!
				Current = Next;
			}

			// Move along Y
			while ( Current.m_EV_ShutterSpeed <= _ShutterSpeed && Current.m_NeighborY[1] != null )
			{
				GridNode	Next = Current.m_NeighborY[1];
				if ( Next.m_EV_ShutterSpeed > _ShutterSpeed )
					break;	// Next node is larger than provided value! We have our start node along Y!
				Current = Next;
			}

			// Move along Z
			while ( Current.m_EV_Aperture <= _Aperture && Current.m_NeighborZ[1] != null )
			{
				GridNode	Next = Current.m_NeighborZ[1];
				if ( Next.m_EV_Aperture > _Aperture )
					break;	// Next node is larger than provided value! We have our start node along X!
				Current = Next;
			}

			return Current;
		}

		#endregion
	}
}