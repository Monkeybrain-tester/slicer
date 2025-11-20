using Godot;
using System;
using System.Collections.Generic;

public static class SliceMeshUtility
{
	public struct SliceResult
	{
		public ArrayMesh FrontMesh;                 // (optional) kept if you use true mesh splitting
		public ArrayMesh BackMesh;                  // (optional)
		public List<Vector3> SectionLoop;           // unordered cross-section points (polygonal areas)
		public List<Vector3[]> SectionSegments;     // line segments that lie exactly on the slice plane
	}

	/// <summary>
	/// Very lightweight slicer that finds plane intersections:
	/// - Adds edge intersection points into SectionLoop (later sorted into polygons by caller)
	/// - Detects edges fully on the plane and adds them into SectionSegments (for thin geometry)
	/// NOTE: Does not actually build FrontMesh/BackMesh (kept null here).
	/// </summary>
	public static SliceResult Slice(ArrayMesh mesh, Plane plane)
	{
		var result = new SliceResult
		{
			FrontMesh = null,
			BackMesh = null,
			SectionLoop = new List<Vector3>(),
			SectionSegments = new List<Vector3[]>()
		};

		if (mesh == null) return result;

		// Find first triangle surface
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
		if (triSurface == -1) return result;

		var mdt = new MeshDataTool();
		if (mdt.CreateFromSurface(mesh, triSurface) != Error.Ok) return result;

		// Gather verts
		var verts = new List<Vector3>(mdt.GetVertexCount());
		for (int i = 0; i < mdt.GetVertexCount(); i++)
			verts.Add(mdt.GetVertex(i));

		// Epsilon for “coplanar”
		const float eps = 1e-4f;

		// Walk all faces, test edges
		for (int f = 0; f < mdt.GetFaceCount(); f++)
		{
			int ia = mdt.GetFaceVertex(f, 0);
			int ib = mdt.GetFaceVertex(f, 1);
			int ic = mdt.GetFaceVertex(f, 2);

			Vector3 a = verts[ia];
			Vector3 b = verts[ib];
			Vector3 c = verts[ic];

			AddEdge(a, b, plane, eps, result);
			AddEdge(b, c, plane, eps, result);
			AddEdge(c, a, plane, eps, result);
		}

		return result;
	}

	private static void AddEdge(Vector3 a, Vector3 b, Plane plane, float eps, SliceResult result)
	{
		float da = plane.DistanceTo(a);
		float db = plane.DistanceTo(b);

		bool onA = MathF.Abs(da) <= eps;
		bool onB = MathF.Abs(db) <= eps;

		// Entire edge lies on plane → add a segment so we can build 2D collision from lines
		if (onA && onB)
		{
			result.SectionSegments.Add(new[] { a, b });
			return;
		}

		// Proper crossing → add intersection point
		if (da * db < 0.0f)
		{
			float t = da / (da - db);
			Vector3 p = a + (b - a) * t;
			result.SectionLoop.Add(p);
		}
	}

	/// <summary>Optional helper if you need box-plane intersections elsewhere.</summary>
	public static List<Vector3> IntersectAabbWithPlane(Vector3[] corners, Plane plane)
	{
		var hits = new List<Vector3>();
		int[,] edges = {
			{0,1},{1,3},{3,2},{2,0},
			{4,5},{5,7},{7,6},{6,4},
			{0,4},{1,5},{2,6},{3,7}
		};
		for (int i = 0; i < edges.GetLength(0); i++)
		{
			Vector3 a = corners[edges[i,0]];
			Vector3 b = corners[edges[i,1]];
			float da = plane.DistanceTo(a);
			float db = plane.DistanceTo(b);
			if (da == 0f && db == 0f)
			{
				hits.Add(a); hits.Add(b);
			}
			else if (da * db < 0f)
			{
				float t = da / (da - db);
				hits.Add(a + (b - a) * t);
			}
		}
		return hits;
	}
}
