using Godot;
using System;

public partial class MainMenu : Control
{
	public override void _Ready()
	{
		// Make sure these paths match your scene tree nodes
		var playBtn  = GetNode<Button>("MarginContainer/MarginContainer/VBoxContainer/PlayButton");
		var selectBtn = GetNode<Button>("MarginContainer/MarginContainer/VBoxContainer/LevelSelectButton");
		var quitBtn  = GetNode<Button>("MarginContainer/MarginContainer/VBoxContainer/QuitButton");

		playBtn.Pressed  += OnPlay;
		selectBtn.Pressed += OnLevelSelect;
		quitBtn.Pressed  += OnQuit;
	}

	private SaveManager GetSave()
	{
		return GetNodeOrNull<SaveManager>("/root/SaveManager");
	}

	private void OnPlay()
	{
		var save = GetSave();
		if (save == null)
		{
			GD.PushWarning("[MainMenu] SaveManager not found. Loading Level1.");
			GetTree().ChangeSceneToFile("res://Levels/Level1.tscn");
			return;
		}

		int max = save.GetMaxUnlocked();              // e.g. 1 on first run
		string path = save.GetLevelPath(max);         // highest unlocked level
		var err = GetTree().ChangeSceneToFile(path);
		if (err != Error.Ok)
		{
			GD.PushError($"[MainMenu] Failed to load {path}. Check that it exists.");
		}
	}

	private void OnLevelSelect()
	{
		const string path = "res://UI/LevelSelect.tscn";
		var err = GetTree().ChangeSceneToFile(path);
		if (err != Error.Ok)
			GD.PushError($"[MainMenu] Cannot open '{path}'.");
	}

	private void OnQuit()
	{
		GetTree().Quit();
	}
}
