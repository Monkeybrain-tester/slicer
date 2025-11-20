// res://Levels/LevelLoader.cs
using Godot;

public partial class LevelLoader : Node {
	private Node _currentLevel;

	public void LoadLevel(PackedScene scene) {
		if (_currentLevel != null) {
			_currentLevel.QueueFree();
			_currentLevel = null;
		}
		_currentLevel = scene.Instantiate();
		GetTree().Root.AddChild(_currentLevel);
		GetTree().CurrentScene = _currentLevel as Node; // optional
	}

	public void LoadLevel(string path) => LoadLevel(GD.Load<PackedScene>(path));
}
