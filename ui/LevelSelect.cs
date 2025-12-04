using Godot;
using System;

public partial class LevelSelect : Control
{
	[Export] public int TotalLevels = 10;   // set in Inspector

	public override void _Ready()
	{
		var grid = GetNode<GridContainer>("MarginContainer/MarginContainer/VBoxContainer/Grid");
		var back = GetNode<Button>("MarginContainer/MarginContainer/VBoxContainer/BackButton");
		back.Pressed += OnBack;

		var save = GetNodeOrNull<SaveManager>("/root/SaveManager");
		int maxUnlocked = save?.GetMaxUnlocked() ?? 1;

		for (int i = 1; i <= TotalLevels; i++)
		{
			var btn = new Button
			{
				Text = $"Level {i}",
				Disabled = i > maxUnlocked
			};

			int idx = i; // capture
			btn.Pressed += () =>
			{
				string path = save?.GetLevelPath(idx) ?? $"res://Levels/Level{idx}.tscn";
				var err = GetTree().ChangeSceneToFile(path);
				if (err != Error.Ok)
					GD.PushError($"[LevelSelect] Failed to load {path}");
			};

			grid.AddChild(btn);
		}
	}

	private void OnBack()
	{
		var err = GetTree().ChangeSceneToFile("res://ui/MainMenu.tscn");
		if (err != Error.Ok)
			GD.PushError("[LevelSelect] Could not load MainMenu.tscn");
	}
}
