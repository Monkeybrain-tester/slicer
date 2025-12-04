using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

public partial class SaveManager : Node
{
	private const string SavePath = "user://save.json";

	private SaveData _data = new();

	// Optional: explicitly list level scenes in order (index 1 = first entry).
	// You can edit this in the inspector once the script is attached to a node or autoload.
	[Export]
	public Godot.Collections.Array<string> LevelPaths = new()
	{
		"res://Levels/Level1.tscn",
		"res://Levels/Level2.tscn",
		"res://Levels/Level3.tscn"
		// add more as needed
	};

	public override void _Ready()
	{
		LoadSave();
	}

	// ---- PUBLIC API ----

	public int GetMaxUnlocked()
	{
		return Mathf.Max(1, _data.HighestUnlockedLevel);
	}

	public bool IsUnlocked(int levelIndex)
	{
		return levelIndex <= GetMaxUnlocked();
	}

	public void UnlockLevel(int levelIndex)
	{
		if (levelIndex > _data.HighestUnlockedLevel)
		{
			_data.HighestUnlockedLevel = levelIndex;
			Save();
		}
	}

	public void MarkLevelCompleted(int levelIndex)
	{
		if (!_data.CompletedLevels.Contains(levelIndex))
			_data.CompletedLevels.Add(levelIndex);

		// Unlock next level (Level 1 completion unlocks Level 2, etc.)
		int nextLevel = levelIndex + 1;
		UnlockLevel(nextLevel);
		Save();
	}

	public bool IsLevelCompleted(int levelIndex)
	{
		return _data.CompletedLevels.Contains(levelIndex);
	}

	public void ResetProgress()
	{
		_data = new SaveData();
		Save();
	}

	public string GetLevelPath(int levelIndex)
	{
		// 1-based level indices
		if (LevelPaths != null &&
			levelIndex >= 1 &&
			levelIndex <= LevelPaths.Count)
		{
			return LevelPaths[levelIndex - 1];
		}

		// fallback naming convention if you prefer:
		return $"res://Levels/Level{levelIndex}.tscn";
	}

	// ---- LOAD / SAVE ----

	private void LoadSave()
	{
		var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			GD.Print("[SaveManager] No save found. Creating default.");
			_data = new SaveData();
			Save();
			return;
		}

		try
		{
			string json = file.GetAsText();
			var d = JsonSerializer.Deserialize<SaveData>(json);
			_data = d ?? new SaveData();
		}
		catch (Exception e)
		{
			GD.PushWarning($"[SaveManager] Failed to load save: {e.Message}. Resetting.");
			_data = new SaveData();
			Save();
		}
	}

	private void Save()
	{
		var options = new JsonSerializerOptions { WriteIndented = true };
		string json = JsonSerializer.Serialize(_data, options);

		using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
		file.StoreString(json);

		GD.Print($"[SaveManager] Saved to {SavePath}. HighestUnlocked={_data.HighestUnlockedLevel}");
	}
}
