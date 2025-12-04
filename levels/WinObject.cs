using Godot;
using System;

/// <summary>
/// WinObject: a visible level goal.
/// Attach this script to an Area3D that has a MeshInstance3D + CollisionShape3D.
/// When Player1 touches it:
///  - Marks ThisLevelIndex as completed in SaveManager
///  - Unlocks the next level
///  - Loads the next level (or a custom scene / main menu)
/// </summary>
public partial class WinObject : Area3D
{
	[Export] public int ThisLevelIndex = 1;

	// If true, after marking completion and unlocking, we load the highest unlocked level.
	[Export] public bool LoadNextUnlockedLevel = true;

	// If not empty, we override destination and go here instead (e.g. main menu).
	[Export] public string OverrideNextScenePath = "";

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
	}

	private void OnBodyEntered(Node3D body)
	{
		// Only trigger for your 3D player
		if (body is not Player1)
			return;

		var save = GetNodeOrNull<SaveManager>("/root/SaveManager");

		if (save != null)
		{
			// mark this one as completed, unlocks next inside
			save.MarkLevelCompleted(ThisLevelIndex);
		}
		else
		{
			GD.PushWarning("[WinObject] SaveManager not found. Proceeding with basic scene change.");
		}

		// Decide where to go
		string nextPath;

		if (!string.IsNullOrEmpty(OverrideNextScenePath))
		{
			nextPath = OverrideNextScenePath;
		}
		else if (LoadNextUnlockedLevel && save != null)
		{
			int max = save.GetMaxUnlocked();
			nextPath = save.GetLevelPath(max);
		}
		else
		{
			// Fallback: go to main menu if nothing else specified
			nextPath = "res://UI/MainMenu.tscn";
		}

		var err = GetTree().ChangeSceneToFile(nextPath);
		if (err != Error.Ok)
			GD.PushError($"[WinObject] Failed to load next scene: {nextPath}");
	}
}
