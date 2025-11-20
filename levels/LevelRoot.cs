// res://Levels/LevelRoot.cs
using Godot;

public partial class LevelRoot : Node3D {
	[Export] public NodePath PlayerPath;
	[Export] public NodePath SpawnPath;

	public override void _Ready() {
		var player = GetNodeOrNull<Node3D>(PlayerPath);
		var spawn = GetNodeOrNull<Node3D>(SpawnPath);
		if (player != null && spawn != null) {
			player.GlobalTransform = spawn.GlobalTransform;
		}
	}
}
