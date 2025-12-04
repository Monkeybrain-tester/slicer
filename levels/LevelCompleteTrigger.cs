using Godot;
using System;

/// <summary>
/// Attach this to an Area3D placed at the end of a level.
/// When the player enters, it marks this level as completed in SaveManager,
/// unlocks the next level, and changes scene.
/// </summary>
public partial class LevelCompleteTrigger : Area3D
{
	[Export] public int ThisLevelIndex = 1;

	// If true, we will automatically load the newly highest-unlocked level.
	// For Level 1: unlocks 2 and loads Level 2, etc.
	[Export] public bool LoadNextUnlockedLevel = true;

	// Optional override: if you want to go back to a menu instead, set this.
	[Export] public string OverrideNextScenePath = "";

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
	}

	private void OnBodyEntered(Node3D body)
	{
		// Only react to the player
		if (body is not Player1) return;

		var save = GetNodeOrNull<SaveManager>("/root/SaveManager");
		if (save == null)
		{
			GD.PushWarning("[LevelCompleteTrigger] SaveManager not found. Scene change only.");
			ChangeSceneFallback();
			return;
		}

		// Mark this level as completed & unlock next
		save.MarkLevelCompleted(ThisLevelIndex);

		if (!string.IsNullOrEmpty(OverrideNextScenePath))
		{
			GetTree().ChangeSceneToFile(OverrideNextScenePath);
			return;
		}

		if (LoadNextUnlockedLevel)
		{
			int max = save.GetMaxUnlocked();
			string path = save.GetLevelPath(max);
			var err = GetTree().ChangeSceneToFile(path);
			if (err != Error.Ok)
				GD.PushError($"[LevelCompleteTrigger] Failed to load {path}");
		}
		else
		{
			// default: return to main menu
			ChangeSceneFallback();
		}
	}

	private void ChangeSceneFallback()
	{
		var err = GetTree().ChangeSceneToFile("res://UI/MainMenu.tscn");
		if (err != Error.Ok)
			GD.PushError("[LevelCompleteTrigger] Failed to load MainMenu.tscn");
	}
}
