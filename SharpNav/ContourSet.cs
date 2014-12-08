﻿#region License
/**
 * Copyright (c) 2013-2014 Robert Rouhani <robert.rouhani@gmail.com> and other contributors (see CONTRIBUTORS file).
 * Licensed under the MIT License - https://raw.github.com/Robmaister/SharpNav/master/LICENSE
 */
#endregion

using System;
using System.Collections.Generic;

using SharpNav.Geometry;

namespace SharpNav
{
	//TODO should this be ISet<Contour>? Are the extra methods useful?
	
	/// <summary>
	/// A set of contours around the regions of a <see cref="CompactHeightfield"/>, used as the edges of a
	/// <see cref="PolyMesh"/>.
	/// </summary>
	public class ContourSet : ICollection<Contour>
	{
		private List<Contour> contours;
		private BBox3 bounds;
		private float cellSize;
		private float cellHeight;
		private int width;
		private int height;
		private int borderSize;

		/// <summary>
		/// Initializes a new instance of the <see cref="ContourSet"/> class.
		/// </summary>
		/// <param name="compactField">The <see cref="CompactHeightField"/> containing regions.</param>
		/// <param name="settings">The settings to build with.</param>
		public ContourSet(CompactHeightfield compactField, NavMeshGenerationSettings settings)
			: this(compactField, settings.MaxEdgeError, settings.MaxEdgeLength, settings.ContourFlags)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ContourSet"/> class by tracing edges around the regions generated by the
		/// <see cref="CompactHeightfield"/>.
		/// </summary>
		/// <param name="compactField">The <see cref="CompactHeightfield"/> containing regions.</param>
		/// <param name="maxError">The maximum amount of error allowed in simplification.</param>
		/// <param name="maxEdgeLen">The maximum length of an edge.</param>
		/// <param name="buildFlags">The settings for how contours should be built.</param>
		public ContourSet(CompactHeightfield compactField, float maxError, int maxEdgeLen, ContourBuildFlags buildFlags)
		{
			//copy the CompactHeightfield data into ContourSet
			this.bounds = compactField.Bounds;

			if (compactField.BorderSize > 0)
			{
				//remove offset
				float pad = compactField.BorderSize * compactField.CellSize;
				this.bounds.Min.X += pad;
				this.bounds.Min.Z += pad;
				this.bounds.Max.X -= pad;
				this.bounds.Max.Z -= pad;
			}

			this.cellSize = compactField.CellSize;
			this.cellHeight = compactField.CellHeight;
			this.width = compactField.Width - compactField.BorderSize * 2;
			this.height = compactField.Height - compactField.BorderSize * 2;
			this.borderSize = compactField.BorderSize;

			int maxContours = Math.Max((int)compactField.MaxRegions, 8);
			contours = new List<Contour>(maxContours);

			byte[] flags = new byte[compactField.Spans.Length];

			//TODO move to CompactHeightField
			//Modify flags array by using the CompactHeightfield data
			//mark boundaries
			for (int y = 0; y < compactField.Length; y++)
			{
				for (int x = 0; x < compactField.Width; x++)
				{
					//loop through all the spans in the cell
					CompactCell c = compactField.Cells[x + y * compactField.Width];
					for (int i = c.StartIndex, end = c.StartIndex + c.Count; i < end; i++)
					{
						CompactSpan s = compactField.Spans[i];

						//set the flag to 0 if the region is a border region or null.
						if (Region.IsBorderOrNull(compactField.Spans[i].Region))
						{
							flags[i] = 0;
							continue;
						}

						//go through all the neighboring cells
						for (var dir = Direction.West; dir <= Direction.South; dir++)
						{
							//obtain region id
							RegionId r = RegionId.Null;
							if (s.IsConnected(dir))
							{
								int dx = x + dir.GetHorizontalOffset();
								int dy = y + dir.GetVerticalOffset();
								int di = compactField.Cells[dx + dy * compactField.Width].StartIndex + CompactSpan.GetConnection(ref s, dir);
								r = compactField.Spans[di].Region;
							}

							//region ids are equal
							if (r == compactField.Spans[i].Region)
							{
								//res marks all the internal edges
								AddEdgeFlag(ref flags[i], dir);
							}
						}

						//flags represents all the nonconnected edges, edges that are only internal
						//the edges need to be between different regions
						FlipEdgeFlags(ref flags[i]); 
					}
				}
			}

			var verts = new List<ContourVertex>();
			var simplified = new List<ContourVertex>();

			for (int y = 0; y < compactField.Length; y++)
			{
				for (int x = 0; x < compactField.Width; x++)
				{
					CompactCell c = compactField.Cells[x + y * compactField.Width];
					for (int i = c.StartIndex, end = c.StartIndex + c.Count; i < end; i++)
					{
						//flags is either 0000 or 1111
						//in other words, not connected at all 
						//or has all connections, which means span is in the middle and thus not an edge.
						if (flags[i] == 0 || flags[i] == 0xf)
						{
							flags[i] = 0;
							continue;
						}

						var spanRef = new CompactSpanReference(x, y, i);
						RegionId reg = compactField[spanRef].Region;
						if (Region.IsBorderOrNull(reg))
							continue;
						
						//reset each iteration
						verts.Clear();
						simplified.Clear();

						//Mark points, which are basis of contous, intially with "verts"
						//Then, simplify "verts" to get "simplified"
						//Finally, clean up the "simplified" data
						WalkContour(compactField, spanRef, flags, verts);
						SimplifyContour(verts, simplified, maxError, maxEdgeLen, buildFlags);
						RemoveDegenerateSegments(simplified);

						if (simplified.Count >= 3)
							contours.Add(new Contour(simplified, verts, reg, compactField.Areas[i], borderSize));
					}
				}
			}

			//Check and merge bad contours
			for (int i = 0; i < contours.Count; i++)
			{
				Contour cont = contours[i];

				//Check if contour is backwards
				if (cont.Area2D < 0)
				{
					//Find another contour to merge with
					int mergeIndex = -1;
					for (int j = 0; j < contours.Count; j++)
					{
						if (i == j)
							continue;

						//Must have at least one vertex, the same region ID, and be going forwards.
						Contour contj = contours[j];
						if (contj.Vertices.Length != 0 && contj.RegionId == cont.RegionId && contj.Area2D > 0)
						{
							mergeIndex = j;
							break;
						}
					}

					//Merge if found.
					if (mergeIndex != -1)
						contours[mergeIndex].MergeWith(cont);
				}
			}
		}

		/// <summary>
		/// Gets the number of <see cref="Contour"/>s in the set.
		/// </summary>
		public int Count
		{
			get
			{
				return contours.Count;
			}
		}

		/// <summary>
		/// Gets the world-space bounding box of the set.
		/// </summary>
		public BBox3 Bounds
		{
			get
			{
				return bounds;
			}
		}

		/// <summary>
		/// Gets the size of a cell in the X and Z axes.
		/// </summary>
		public float CellSize
		{
			get
			{
				return cellSize;
			}
		}

		/// <summary>
		/// Gets the height of a cell in the Y axis.
		/// </summary>
		public float CellHeight
		{
			get
			{
				return cellHeight;
			}
		}

		/// <summary>
		/// Gets the width of the set, not including the border size specified in <see cref="CompactHeightfield"/>.
		/// </summary>
		public int Width
		{
			get
			{
				return width;
			}
		}

		/// <summary>
		/// Gets the height of the set, not including the border size specified in <see cref="CompactHeightfield"/>.
		/// </summary>
		public int Height
		{
			get
			{
				return height;
			}
		}

		/// <summary>
		/// Gets the size of the border.
		/// </summary>
		public int BorderSize
		{
			get
			{
				return borderSize;
			}
		}

		/// <summary>
		/// Gets a value indicating whether the <see cref="ContourSet"/> is read-only.
		/// </summary>
		bool ICollection<Contour>.IsReadOnly
		{
			get { return true; }
		}

		/// <summary>
		/// Checks if a specified <see cref="ContourSet"/> is contained in the <see cref="ContourSet"/>.
		/// </summary>
		/// <param name="item">A contour.</param>
		/// <returns>A value indicating whether the set contains the specified contour.</returns>
		public bool Contains(Contour item)
		{
			return contours.Contains(item);
		}

		/// <summary>
		/// Copies the <see cref="Contour"/>s in the set to an array.
		/// </summary>
		/// <param name="array">The array to copy to.</param>
		/// <param name="arrayIndex">The zero-based index in array at which copying begins.</param>
		public void CopyTo(Contour[] array, int arrayIndex)
		{
			contours.CopyTo(array, arrayIndex);
		}

		/// <summary>
		/// Returns an enumerator that iterates through the entire <see cref="ContourSet"/>.
		/// </summary>
		/// <returns>An enumerator.</returns>
		public IEnumerator<Contour> GetEnumerator()
		{
			return contours.GetEnumerator();
		}

		//TODO support the extra ICollection methods later?

		/// <summary>
		/// Add a new contour to the set
		/// </summary>
		/// <param name="item">The contour to add</param>
		void ICollection<Contour>.Add(Contour item)
		{
			throw new InvalidOperationException();
		}
		
		/// <summary>
		/// (Not implemented) Clear the list
		/// </summary>
		void ICollection<Contour>.Clear()
		{
			throw new InvalidOperationException();
		}

		/// <summary>
		/// (Not implemented) Remove a contour from the set
		/// </summary>
		/// <param name="item">The contour to remove</param>
		/// <returns>throw InvalidOperatorException</returns>
		bool ICollection<Contour>.Remove(Contour item)
		{
			throw new InvalidOperationException();
		}

		/// <summary>
		/// Gets an enumerator that iterates through the set
		/// </summary>
		/// <returns>The enumerator</returns>
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		/// <summary>
		/// Sets the bit for a direction to 1 in a specified byte.
		/// </summary>
		/// <param name="flag">The byte containing flags.</param>
		/// <param name="dir">The direction to add.</param>
		private static void AddEdgeFlag(ref byte flag, Direction dir)
		{
			//flag represented as 4 bits (left bit represents dir = 3, right bit represents dir = 0)
			//default is 0000
			//the |= operation sets each direction bit to 1 (so if dir = 0, 0000 -> 0001)
			flag |= (byte)(1 << (int)dir);
		}

		/// <summary>
		/// Flips all the bits used for flags in a byte.
		/// </summary>
		/// <param name="flag">The byte containing flags.</param>
		private static void FlipEdgeFlags(ref byte flag)
		{
			//flips all the bits in res
			//0000 (completely internal) -> 1111
			//1111 (no internal edges) -> 0000
			flag ^= 0xf;
		}

		/// <summary>
		/// Determines whether the bit for a direction is set in a byte.
		/// </summary>
		/// <param name="flag">The byte containing flags.</param>
		/// <param name="dir">The direction to check for.</param>
		/// <returns>A value indicating whether the flag for the specified direction is set.</returns>
		private static bool IsConnected(ref byte flag, Direction dir)
		{
			//four bits, each bit represents a direction (0 = non-connected, 1 = connected)
			return (flag & (1 << (int)dir)) != 0;
		}

		/// <summary>
		/// Sets the bit for a direction to 0 in a specified byte.
		/// </summary>
		/// <param name="flag">The byte containing flags.</param>
		/// <param name="dir">The direction to remove.</param>
		private static void RemoveEdgeFlag(ref byte flag, Direction dir)
		{
			//say flag = 0110
			//dir = 2 (so 1 << dir = 0100)
			//~dir = 1011
			//flag &= ~dir
			//flag = 0110 & 1011 = 0010
			flag &= (byte)(~(1 << (int)dir)); // remove visited edges
		}

		/// <summary>
		/// Initial generation of the contours
		/// </summary>
		/// <param name="compactField">The compact heightfield to reference.</param>
		/// <param name="spanReference">A referecne to the span to start walking from.</param>
		/// <param name="flags">An array of flags determinining </param>
		/// <param name="points">The vertices of a contour.</param>
		private void WalkContour(CompactHeightfield compactField, CompactSpanReference spanReference, byte[] flags, List<ContourVertex> points)
		{
			Direction dir = Direction.West;

			//find the first direction that has a connection 
			while (!IsConnected(ref flags[spanReference.Index], dir))
				dir++;

			Direction startDir = dir;
			int startIndex = spanReference.Index;

			AreaId area = compactField.Areas[startIndex];

			//TODO make the max iterations value a variable
			int iter = 0;
			while (++iter < 40000)
			{
				// this direction is connected
				if (IsConnected(ref flags[spanReference.Index], dir))
				{
					// choose the edge corner
					bool isBorderVertex;
					bool isAreaBorder = false;

					int px = spanReference.X;
					int py = GetCornerHeight(compactField, spanReference, dir, out isBorderVertex);
					int pz = spanReference.Y;

					switch (dir)
					{
						case Direction.West:
							pz++;
							break;
						case Direction.North:
							px++;
							pz++;
							break;
						case Direction.East:
							px++;
							break;
					}

					RegionId r = 0;
					CompactSpan s = compactField[spanReference];
					if (s.IsConnected(dir))
					{
						int dx = spanReference.X + dir.GetHorizontalOffset();
						int dy = spanReference.Y + dir.GetVerticalOffset();
						int di = compactField.Cells[dx + dy * compactField.Width].StartIndex + CompactSpan.GetConnection(ref s, dir);
						r = compactField.Spans[di].Region;
						if (area != compactField.Areas[di])
							isAreaBorder = true;
					}
					
					// apply flags if neccessary
					if (isBorderVertex)
						Region.SetBorderVertex(ref r);

					if (isAreaBorder)
						Region.SetAreaBorder(ref r);
					
					//save the point
					points.Add(new ContourVertex(px, py, pz, r));

					RemoveEdgeFlag(ref flags[spanReference.Index], dir);	// remove visited edges
					dir = dir.NextClockwise();			// rotate clockwise
				}
				else
				{
					//get a new cell(x, y) and span index(i)
					int di = -1;
					int dx = spanReference.X + dir.GetHorizontalOffset();
					int dy = spanReference.Y + dir.GetVerticalOffset();
					
					CompactSpan s = compactField[spanReference];
					if (s.IsConnected(dir))
					{
						CompactCell dc = compactField.Cells[dx + dy * compactField.Width];
						di = dc.StartIndex + CompactSpan.GetConnection(ref s, dir);
					}
					
					if (di == -1)
					{
						// shouldn't happen
						// TODO if this shouldn't happen, this check shouldn't be necessary.
						throw new InvalidOperationException("Something went wrong");
					}

					spanReference = new CompactSpanReference(dx, dy, di);
					dir = dir.NextCounterClockwise(); // rotate counterclockwise
				}

				if (startIndex == spanReference.Index && startDir == dir)
					break;
			}
		}

		/// <summary>
		/// Helper method for WalkContour function
		/// </summary>
		/// <param name="compactField">The compact heightfield to reference.</param>
		/// <param name="sr">The span to get the corner height for.</param>
		/// <param name="dir">The direction to get the corner height from.</param>
		/// <param name="isBorderVertex">Determine whether the vertex is a border or not.</param>
		/// <returns>The corner height.</returns>
		private int GetCornerHeight(CompactHeightfield compactField, CompactSpanReference sr, Direction dir, out bool isBorderVertex)
		{
			isBorderVertex = false;

			CompactSpan s = compactField[sr];
			int cornerHeight = s.Minimum;
			Direction dirp = dir.NextClockwise(); //new clockwise direction

			uint[] regs = { 0, 0, 0, 0 };

			//combine region and area codes in order to prevent border vertices, which are in between two areas, to be removed 
			regs[0] = (uint)((int)s.Region | ((byte)compactField.Areas[sr.Index] << 16));

			if (s.IsConnected(dir))
			{
				//get neighbor span
				int dx = sr.X + dir.GetHorizontalOffset();
				int dy = sr.Y + dir.GetVerticalOffset();
				int di = compactField.Cells[dx + dy * compactField.Width].StartIndex + CompactSpan.GetConnection(ref s, dir);
				CompactSpan ds = compactField.Spans[di];

				cornerHeight = Math.Max(cornerHeight, ds.Minimum);
				regs[1] = (uint)((int)compactField.Spans[di].Region | ((byte)compactField.Areas[di] << 16));

				//get neighbor of neighbor's span
				if (ds.IsConnected(dirp))
				{
					int dx2 = dx + dirp.GetHorizontalOffset();
					int dy2 = dy + dirp.GetVerticalOffset();
					int di2 = compactField.Cells[dx2 + dy2 * compactField.Width].StartIndex + CompactSpan.GetConnection(ref ds, dirp);
					CompactSpan ds2 = compactField.Spans[di2];

					cornerHeight = Math.Max(cornerHeight, ds2.Minimum);
					regs[2] = (uint)((int)compactField.Spans[di2].Region | ((byte)compactField.Areas[di2] << 16));
				}
			}

			//get neighbor span
			if (s.IsConnected(dirp))
			{
				int dx = sr.X + dirp.GetHorizontalOffset();
				int dy = sr.Y + dirp.GetVerticalOffset();
				int di = compactField.Cells[dx + dy * compactField.Width].StartIndex + CompactSpan.GetConnection(ref s, dirp);
				CompactSpan ds = compactField.Spans[di];

				cornerHeight = Math.Max(cornerHeight, ds.Minimum);
				regs[3] = (uint)((int)compactField.Spans[di].Region | ((byte)compactField.Areas[di] << 16));

				//get neighbor of neighbor's span
				if (ds.IsConnected(dir))
				{
					int dx2 = dx + dir.GetHorizontalOffset();
					int dy2 = dy + dir.GetVerticalOffset();
					int di2 = compactField.Cells[dx2 + dy2 * compactField.Width].StartIndex + CompactSpan.GetConnection(ref ds, dir);
					CompactSpan ds2 = compactField.Spans[di2];

					cornerHeight = Math.Max(cornerHeight, ds2.Minimum);
					regs[2] = (uint)((int)compactField.Spans[di2].Region | ((byte)compactField.Areas[di2] << 16));
				}
			}

			//check if vertex is special edge vertex
			//if so, these vertices will be removed later
			for (int j = 0; j < 4; j++)
			{
				int a = j;
				int b = (j + 1) % 4;
				int c = (j + 2) % 4;
				int d = (j + 3) % 4;

				//the vertex is a border vertex if:
				//two same exterior cells in a row followed by two interior cells and none of the regions are out of bounds
				bool twoSameExteriors = Region.IsBorder((RegionId)regs[a]) && Region.IsBorder((RegionId)regs[b]) && regs[a] == regs[b];
				bool twoSameInteriors = !(Region.IsBorder((RegionId)regs[c]) || Region.IsBorder((RegionId)regs[d]));
				bool intsSameArea = (regs[c] >> 16) == (regs[d] >> 16);
				bool noZeros = regs[a] != 0 && regs[b] != 0 && regs[c] != 0 && regs[d] != 0;
				if (twoSameExteriors && twoSameInteriors && intsSameArea && noZeros)
				{
					isBorderVertex = true;
					break;
				}
			}

			return cornerHeight;
		}

		/// <summary>
		/// Simplify the contours by reducing the number of edges
		/// </summary>
		/// <param name="points">Initial vertices</param>
		/// <param name="simplified">New and simplified vertices</param>
		/// <param name="maxError">Maximum error allowed</param>
		/// <param name="maxEdgeLen">The maximum edge length allowed</param>
		/// <param name="buildFlags">Flags determines how to split the long edges</param>
		private void SimplifyContour(List<ContourVertex> points, List<ContourVertex> simplified, float maxError, int maxEdgeLen, ContourBuildFlags buildFlags)
		{
			//add initial points
			bool hasConnections = false;
			for (int i = 0; i < points.Count; i++)
			{
				if (Region.RemoveFlags(points[i].RegionId) != 0)
				{
					hasConnections = true;
					break;
				}
			}

			if (hasConnections)
			{
				//contour has some portals to other regions
				//add new point to every location where region changes
				for (int i = 0, end = points.Count; i < end; i++)
				{
					int ii = (i + 1) % end;
					bool differentRegions = !Region.IsSameRegion(points[i].RegionId, points[ii].RegionId);
					bool areaBorders = !Region.IsSameArea(points[i].RegionId, points[ii].RegionId);
					
					if (differentRegions || areaBorders)
					{
						simplified.Add(new ContourVertex(points[i], i));
					}
				}
			}

			//add some points if thhere are no connections
			if (simplified.Count == 0)
			{
				//find lower-left and upper-right vertices of contour
				int lowerLeftX = points[0].X;
				int lowerLeftY = points[0].Y;
				int lowerLeftZ = points[0].Z;
				RegionId lowerLeftI = 0;
				
				int upperRightX = points[0].X;
				int upperRightY = points[0].Y;
				int upperRightZ = points[0].Z;
				RegionId upperRightI = 0;
				
				//iterate through points
				for (int i = 0; i < points.Count; i++)
				{
					int x = points[i].X;
					int y = points[i].Y;
					int z = points[i].Z;
					
					if (x < lowerLeftX || (x == lowerLeftX && z < lowerLeftZ))
					{
						lowerLeftX = x;
						lowerLeftY = y;
						lowerLeftZ = z;
						lowerLeftI = (RegionId)i;
					}
					
					if (x > upperRightX || (x == upperRightX && z > upperRightZ))
					{
						upperRightX = x;
						upperRightY = y;
						upperRightZ = z;
						upperRightI = (RegionId)i;
					}
				}
				
				//save the points
				simplified.Add(new ContourVertex(lowerLeftX, lowerLeftY, lowerLeftZ, lowerLeftI));
				simplified.Add(new ContourVertex(upperRightX, upperRightY, upperRightZ, upperRightI));
			}

			//add points until all points are within erorr tolerance of simplified slope
			int numPoints = points.Count;
			for (int i = 0; i < simplified.Count;)
			{
				int ii = (i + 1) % simplified.Count;

				//obtain (x, z) coordinates, along with region id
				int ax = simplified[i].X;
				int az = simplified[i].Z;
				RegionId ai = simplified[i].RegionId;

				int bx = simplified[ii].X;
				int bz = simplified[ii].Z;
				RegionId bi = simplified[ii].RegionId;

				float maxDeviation = 0;
				int maxi = -1;
				int ci, countIncrement, endi;

				//traverse segment in lexilogical order (try to go from smallest to largest coordinates?)
				if (bx > ax || (bx == ax && bz > az))
				{
					countIncrement = 1;
					ci = (int)(ai + countIncrement) % numPoints;
					endi = (int)bi;
				}
				else
				{
					countIncrement = numPoints - 1;
					ci = (int)(bi + countIncrement) % numPoints;
					endi = (int)ai;
				}

				//tessellate only outer edges or edges between areas
				if (Region.RemoveFlags(points[ci].RegionId) == 0 || Region.IsAreaBorder(points[ci].RegionId))
				{
					//find the maximum deviation
					while (ci != endi)
					{
						float deviation = MathHelper.Distance.PointToSegment2DSquared(points[ci].X, points[ci].Z, ax, az, bx, bz);
						
						if (deviation > maxDeviation)
						{
							maxDeviation = deviation;
							maxi = ci;
						}

						ci = (ci + countIncrement) % numPoints;
					}
				}

				//If max deviation is larger than accepted error, add new point
				if (maxi != -1 && maxDeviation > (maxError * maxError))
				{
					//add extra space to list
					simplified.Add(new ContourVertex(0, 0, 0, 0));

					//make space for new point by shifting elements to the right
					//ex: element at index 5 is now at index 6, since array[6] takes the value of array[6 - 1]
					for (int j = simplified.Count - 1; j > i; j--)
					{
						simplified[j] = simplified[j - 1];
					}

					//add point 
					simplified[i + 1] = new ContourVertex(points[maxi], maxi);
				}
				else
				{
					i++;
				}
			}

			//split too long edges
			if (maxEdgeLen > 0 && (buildFlags & (ContourBuildFlags.TessellateAreaEdges | ContourBuildFlags.TessellateWallEdges)) != 0)
			{
				for (int i = 0; i < simplified.Count;)
				{
					int ii = (i + 1) % simplified.Count;

					//get (x, z) coordinates along with region id
					int ax = simplified[i].X;
					int az = simplified[i].Z;
					RegionId ai = simplified[i].RegionId;

					int bx = simplified[ii].X;
					int bz = simplified[ii].Z;
					RegionId bi = simplified[ii].RegionId;

					//find maximum deviation from segment
					int maxi = -1;
					int ci = (int)(ai + 1) % numPoints;

					//tessellate only outer edges or edges between areas
					bool tess = false;

					//wall edges
					if ((buildFlags & ContourBuildFlags.TessellateWallEdges) != 0 && Region.RemoveFlags(points[ci].RegionId) == 0)
						tess = true;

					//edges between areas
					if ((buildFlags & ContourBuildFlags.TessellateAreaEdges) != 0 && Region.IsAreaBorder(points[ci].RegionId))
						tess = true;

					if (tess)
					{
						int dx = bx - ax;
						int dz = bz - az;
						if (dx * dx + dz * dz > maxEdgeLen * maxEdgeLen)
						{
							//round based on lexilogical direction (smallest to largest cooridinates, first by x.
							//if x coordinates are equal, then compare z coordinates)
							int n = bi < ai ? (bi + numPoints - ai) : (bi - ai);
							
							if (n > 1)
							{
								if (bx > ax || (bx == ax && bz > az))
									maxi = (int)(ai + n / 2) % numPoints;
								else
									maxi = (int)(ai + (n + 1) / 2) % numPoints;
							}
						}
					}

					//add new point
					if (maxi != -1)
					{
						//add extra space to list
						simplified.Add(new ContourVertex(0, 0, 0, 0));

						//make space for new point by shifting elements to the right
						//ex: element at index 5 is now at index 6, since array[6] takes the value of array[6 - 1]
						for (int j = simplified.Count - 1; j > i; j--)
						{
							simplified[j] = simplified[j - 1];
						}

						//add point
						simplified[i + 1] = new ContourVertex(points[maxi], maxi);
					}
					else
					{
						i++;
					}
				}
			}

			for (int i = 0; i < simplified.Count; i++)
			{
				ContourVertex sv = simplified[i];

				//take edge vertex flag from current raw point and neighbor region from next raw point
				int ai = (int)(sv.RegionId + 1) % numPoints;
				RegionId bi = sv.RegionId;

				//save new region id
				sv.RegionId = (points[ai].RegionId & ((RegionId)Region.IdMask | RegionId.AreaBorder)) | (points[(int)bi].RegionId & RegionId.VertexBorder);

				simplified[i] = sv;
			}
		}

		/// <summary>
		/// Removes degenerate segments from a simplified contour.
		/// </summary>
		/// <param name="simplified">The simplified contour.</param>
		private void RemoveDegenerateSegments(List<ContourVertex> simplified)
		{
			//remove adjacent vertices which are equal on the xz-plane
			for (int i = 0; i < simplified.Count; i++)
			{
				int ni = i + 1;
				if (ni >= simplified.Count)
					ni = 0;

				if (simplified[i].X == simplified[ni].X &&
					simplified[i].Z == simplified[ni].Z)
				{
					//remove degenerate segment
					simplified.RemoveAt(i);
					i--;
				}
			}
		}
	}
}
