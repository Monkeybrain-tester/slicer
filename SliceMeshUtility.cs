using Godot;
using System;
using System.Collections.Generic;

public static class SliceMeshUtility
{
	public struct SliceResult
	{
		public ArrayMesh FrontMesh;   // (unused for now)
		public ArrayMesh BackMesh;    // (unused for now)
		public List<Vector3> SectionLoop;
	}

	public static SliceResult Slice(ArrayMesh mesh, Plane plane)
	{
		var result = new SliceResult
		{
			FrontMesh = null,
			BackMesh = null,
			SectionLoop = new List<Vector3>()
		};

		if (mesh == null) return result;

		// Find the first surface that is Triangles
		int triSurface = -1;
		int sc = mesh.GetSurfaceCount();
		for (int s = 0; s < sc; s++)
		{
			if (mesh.SurfaceGetPrimitiveType(s) == Mesh.PrimitiveType.Triangles)
			{
				triSurface = s;
				break;
			}
		}
		if (triSurface == -1)
		{
			// No triangle surface to read — nothing to slice
			return result;
		}

		// Read triangles via MeshDataTool
		var mdt = new MeshDataTool();
		var err = mdt.CreateFromSurface(mesh, triSurface);
		if (err != Error.Ok) return result;

		// Collect vertices
		var verts = new List<Vector3>(mdt.GetVertexCount());
		for (int i = 0; i < mdt.GetVertexCount(); i++)
			verts.Add(mdt.GetVertex(i));

		// Build section loop by intersecting triangle edges with plane
		var loopPts = new List<Vector3>();

		for (int f = 0; f < mdt.GetFaceCount(); f++)
		{
			int ia = mdt.GetFaceVertex(f, 0);
			int ib = mdt.GetFaceVertex(f, 1);
			int ic = mdt.GetFaceVertex(f, 2);

			Vector3 a = verts[ia];
			Vector3 b = verts[ib];
			Vector3 c = verts[ic];

			AddIntersection(a, b, plane, loopPts);
			AddIntersection(b, c, plane, loopPts);
			AddIntersection(c, a, plane, loopPts);
		}

		result.SectionLoop = loopPts;
		return result;
	}

	private static void AddIntersection(Vector3 a, Vector3 b, Plane plane, List<Vector3> outList)
	{
		float da = plane.DistanceTo(a);
		float db = plane.DistanceTo(b);
		// Strict sign change = segment crosses plane
		if (da == 0f && db == 0f)
		{
			// Entire edge sits on plane — push both (cheap fallback)
			outList.Add(a);
			outList.Add(b);
			return;
		}
		if (da * db < 0.0f)
		{
			float t = da / (da - db);
			Vector3 p = a + (b - a) * t;
			outList.Add(p);
		}
	}

	/// <summary>
	/// Intersects an arbitrary box (8 world-space corners) with a plane and returns intersection points.
	/// </summary>
	public static List<Vector3> IntersectAabbWithPlane(Vector3[] corners, Plane plane)
	{
		var hits = new List<Vector3>();

		int[,] edges = {
			{0,1},{1,3},{3,2},{2,0}, // bottom ring
			{4,5},{5,7},{7,6},{6,4}, // top ring
			{0,4},{1,5},{2,6},{3,7}  // verticals
		};

		for (int i = 0; i < edges.GetLength(0); i++)
		{
			Vector3 a = corners[edges[i,0]];
			Vector3 b = corners[edges[i,1]];
			float da = plane.DistanceTo(a);
			float db = plane.DistanceTo(b);

			if (da == 0f && db == 0f)
			{
				// Edge lies on plane; add both ends
				hits.Add(a);
				hits.Add(b);
				continue;
			}
			if (da * db < 0f)
			{
				float t = da / (da - db);
				hits.Add(a + (b - a) * t);
			}
		}

		return hits;
	}
}
