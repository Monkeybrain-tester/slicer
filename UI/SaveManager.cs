using Godot;
using System;
using System.Text.Json;

public partial class SaveManager : Node
{
	private const string SavePath = "user://save.json";

	// Use the external SaveData class you already have in UI/SaveData.cs
	private SaveData _data = new();

	public override void _Ready()
	{
		LoadSave();
	}

	public int GetMaxUnlocked() => Math.Max(1, _data.HighestUnlockedLevel);

	public bool IsUnlocked(int levelIndex) => levelIndex <= GetMaxUnlocked();

	public void UnlockLevel(int levelIndex)
	{
		if (levelIndex > _data.HighestUnlockedLevel)
		{
			_data.HighestUnlockedLevel = levelIndex;
			Save();
		}
	}

	public void ResetProgress()
	{
		_data = new SaveData();
		Save();
	}

	public string GetLevelPath(int levelIndex)
	{
		// Change this if your levels live elsewhere or have different names
		return $"res://Levels/Level{levelIndex}.tscn";
	}

	private void LoadSave()
	{
		using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			_data = new SaveData(); // defaults
			Save();                 // write first save file
			return;
		}

		try
		{
			var json = file.GetAsText();
			var d = JsonSerializer.Deserialize<SaveData>(json);
			_data = d ?? new SaveData();
		}
		catch (Exception e)
		{
			GD.PushWarning($"Save load failed: {e.Message}. Resetting save.");
			_data = new SaveData();
			Save();
		}
	}

	private void Save()
	{
		var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
		using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
		file.StoreString(json);
	}
}
