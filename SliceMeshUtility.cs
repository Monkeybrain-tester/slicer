using Godot;
using System;
using System.Collections.Generic;



public static class SliceMeshUtility
{
	public struct SliceResult
	{
		public ArrayMesh FrontMesh;
		public ArrayMesh BackMesh;
		public List<Vector3> SectionLoop;           // kept for legacy (may be empty)
		public List<(Vector3 A, Vector3 B)> SectionSegments; // NEW: exact line segments on the plane
	}

	public static SliceResult Slice(ArrayMesh mesh, Plane plane)
	{
		var result = new SliceResult
		{
			FrontMesh = null,
			BackMesh = null,
			SectionLoop = new List<Vector3>(),
			SectionSegments = new List<(Vector3, Vector3)>()
		};
		if (mesh == null) return result;

		// Find a triangle surface
		int triSurface = -1;
		for (int s = 0; s < mesh.GetSurfaceCount(); s++)
			if (mesh.SurfaceGetPrimitiveType(s) == Mesh.PrimitiveType.Triangles) { triSurface = s; break; }
		if (triSurface == -1) return result;

		var mdt = new MeshDataTool();
		if (mdt.CreateFromSurface(mesh, triSurface) != Error.Ok) return result;

		// Read verts
		var verts = new List<Vector3>(mdt.GetVertexCount());
		for (int i = 0; i < mdt.GetVertexCount(); i++) verts.Add(mdt.GetVertex(i));

		// For each triangle, compute intersection
		for (int f = 0; f < mdt.GetFaceCount(); f++)
		{
			int ia = mdt.GetFaceVertex(f, 0);
			int ib = mdt.GetFaceVertex(f, 1);
			int ic = mdt.GetFaceVertex(f, 2);

			Vector3 a = verts[ia];
			Vector3 b = verts[ib];
			Vector3 c = verts[ic];

			// intersect edges with plane
			var hits = new List<Vector3>(3);
			TryEdge(a, b, plane, hits);
			TryEdge(b, c, plane, hits);
			TryEdge(c, a, plane, hits);

			// If exactly two distinct points, that’s a segment on the slice.
			if (hits.Count >= 2)
			{
				// Deduplicate near-equal
				var p0 = hits[0]; var p1 = hits[1];
				if ((p1 - p0).LengthSquared() > 1e-10f)
					result.SectionSegments.Add((p0, p1));
			}

			// (optional legacy points bag)
			foreach (var h in hits) result.SectionLoop.Add(h);
		}

		return result;
	}

	private static void TryEdge(Vector3 a, Vector3 b, Plane plane, List<Vector3> outPts)
	{
		float da = plane.DistanceTo(a);
		float db = plane.DistanceTo(b);

		if (Mathf.IsZeroApprox(da) && Mathf.IsZeroApprox(db))
		{
			// Edge lies on plane: push both ends (rare; degenerate)
			outPts.Add(a); outPts.Add(b);
			return;
		}
		if (da * db < 0f)
		{
			float t = da / (da - db);
			outPts.Add(a + (b - a) * t);
		}
	}
}
