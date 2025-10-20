// SliceExample.cs
using Godot;

public partial class SliceExample : Node3D
{
	[Export] private NodePath _targetMeshPath;
	private MeshInstance3D _target;

	public override void _Ready()
	{
		_target = GetNode<MeshInstance3D>(_targetMeshPath);
	}

	public override void _Input(InputEvent e)
	{
		if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
		{
			// define plane through player looking forward
			var player = GetNode<CharacterBody3D>("../Player1");
			Vector3 origin = player.GlobalPosition;
			Vector3 normal = -player.GlobalTransform.Basis.Z;
			Plane slicePlane = new Plane(normal, origin.Dot(normal));

			var mesh = _target.Mesh as ArrayMesh;
			var res = SliceMeshUtility.Slice(mesh, slicePlane);

			// Replace target mesh with front side
			_target.Mesh = res.FrontMesh;

			// Optional: create a visible slice face
			var cap = new MeshInstance3D
			{
				Mesh = BuildCap(res.SectionLoop, normal)
			};
			AddChild(cap);
		}
	}

private ArrayMesh BuildCap(System.Collections.Generic.List<Vector3> loop, Vector3 normal)
{
	if (loop == null || loop.Count < 3) return null;

	var st = new SurfaceTool();
	st.Begin(Mesh.PrimitiveType.Triangles);

	// naive fan around centroid — good for visualization
	Vector3 center = Vector3.Zero;
	foreach (var p in loop) center += p;
	center /= loop.Count;

	for (int i = 0; i < loop.Count; i++)
	{
		var a = loop[i];
		var b = loop[(i + 1) % loop.Count];

		st.SetNormal(normal); st.AddVertex(center);
		st.SetNormal(normal); st.AddVertex(a);
		st.SetNormal(normal); st.AddVertex(b);
	}
	return st.Commit();
}


}
