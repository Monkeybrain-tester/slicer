using Godot;
using System;

public partial class LevelSelect : Control
{
	[Export] public int TotalLevels = 10;   // change to match your project

	public override void _Ready()
	{
		var grid = GetNode<GridContainer>("MarginContainer/VBoxContainer/Grid");
		var back = GetNode<Button>("MarginContainer/VBoxContainer/BackButton");
		back.Pressed += () => GetTree().ChangeSceneToFile("res://UI/MainMenu.tscn");

		var save = GetNodeOrNull<Node>("/root/SaveManager") as SaveManager;
		int maxUnlocked = save?.GetMaxUnlocked() ?? 1;

		for (int i = 1; i <= TotalLevels; i++)
		{
			var btn = new Button { Text = $"Level {i}", Disabled = i > maxUnlocked };
			int idx = i; // capture
			btn.Pressed += () =>
			{
				string path = save?.GetLevelPath(idx) ?? $"res://Levels/level{idx}.tscn";
				var err = GetTree().ChangeSceneToFile(path);
				if (err != Error.Ok) GD.PushError($"Failed to load {path}");
			};
			grid.AddChild(btn);
		}
	}
}
