using Godot;
using System;
using System.Collections.Generic;

public static class SliceMeshUtility
{
	public struct SliceResult
	{
		public ArrayMesh FrontMesh;
		public ArrayMesh BackMesh;
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

		MeshDataTool mdt = new MeshDataTool();
		mdt.CreateFromSurface(mesh, 0);

		var verts = new List<Vector3>();
		for (int i = 0; i < mdt.GetVertexCount(); i++)
			verts.Add(mdt.GetVertex(i));

		var tris = new List<int>();
		for (int i = 0; i < mdt.GetFaceCount(); i++)
		{
			for (int j = 0; j < 3; j++)
				tris.Add(mdt.GetFaceVertex(i, j));
		}

		// Simple plane split
		List<Vector3> frontVerts = new List<Vector3>();
		List<Vector3> backVerts = new List<Vector3>();

		for (int i = 0; i < verts.Count; i++)
		{
			Vector3 v = verts[i];
			float d = plane.DistanceTo(v);
			if (d >= 0) frontVerts.Add(v);
			if (d <= 0) backVerts.Add(v);
		}

		// Simple Section loop = all edges crossing the plane
		for (int i = 0; i < tris.Count; i += 3)
		{
			Vector3 a = verts[tris[i]];
			Vector3 b = verts[tris[i + 1]];
			Vector3 c = verts[tris[i + 2]];

			AddIntersection(a, b, plane, result.SectionLoop);
			AddIntersection(b, c, plane, result.SectionLoop);
			AddIntersection(c, a, plane, result.SectionLoop);
		}

		// minimal front mesh rebuild
		if (frontVerts.Count > 2)
		{
			SurfaceTool st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Points);
			foreach (var v in frontVerts)
				st.AddVertex(v);
			result.FrontMesh = st.Commit();
		}

		if (backVerts.Count > 2)
		{
			SurfaceTool st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Points);
			foreach (var v in backVerts)
				st.AddVertex(v);
			result.BackMesh = st.Commit();
		}

		return result;
	}

	private static void AddIntersection(Vector3 a, Vector3 b, Plane plane, List<Vector3> outList)
	{
		float da = plane.DistanceTo(a);
		float db = plane.DistanceTo(b);
		if (da * db < 0.0f)
		{
			float t = da / (da - db);
			Vector3 p = a + (b - a) * t;
			outList.Add(p);
		}
	}

	/// <summary>
	/// Intersects an arbitrary box (8 corners) with a plane and returns intersection points.
	/// </summary>
	public static List<Vector3> IntersectAabbWithPlane(Vector3[] corners, Plane plane)
{
	// corners length must be 8: endpoints 0..7 from Aabb.GetEndpoint(i)
	// edges list for a box (12 edges):
	int[,] edges = {
		{0,1},{1,3},{3,2},{2,0}, // bottom
		{4,5},{5,7},{7,6},{6,4}, // top
		{0,4},{1,5},{2,6},{3,7}  // pillars
	};

	const float EPS = 1e-5f;
	var hits = new List<Vector3>();

	// Helper to push unique points (avoid duplicates)
	void PushUnique(Vector3 p) {
		foreach (var q in hits)
		{
			if ((q - p).LengthSquared() < 1e-6f) return;
		}
		hits.Add(p);
	}

	// First handle edges
	for (int i = 0; i < edges.GetLength(0); i++)
	{
		Vector3 a = corners[edges[i, 0]];
		Vector3 b = corners[edges[i, 1]];
		float da = plane.DistanceTo(a);
		float db = plane.DistanceTo(b);

		bool aOn = Mathf.Abs(da) <= EPS;
		bool bOn = Mathf.Abs(db) <= EPS;

		if (aOn && bOn)
		{
			// Entire edge is coplanar → add both endpoints
			PushUnique(a);
			PushUnique(b);
			continue;
		}

		if (aOn) { PushUnique(a); continue; }
		if (bOn) { PushUnique(b); continue; }

		// Proper crossing
		if (da * db < 0f)
		{
			float t = da / (da - db);
			Vector3 p = a + (b - a) * t;
			PushUnique(p);
		}
	}

	// If the plane exactly coincides with a face, edges above added 4 corners.
	// If we got < 3 points, there is no polygonal section.
	if (hits.Count < 3) return hits;

	// Optional cleanup: project to plane’s local basis and convex-hull sort
	// (We rely on SliceManager.SortLoopOnPlane later, so just return hits.)
	return hits;
}

}
