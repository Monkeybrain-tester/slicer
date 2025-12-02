using Godot;

public partial class MainMenu : Control
{
	public override void _Ready()
	{
		// Adjust these paths if your hierarchy differs
		WireButton("MarginContainer/VBoxContainer/PlayButton", OnPlay);
		WireButton("MarginContainer/VBoxContainer/LevelSelectButton", OnLevelSelect);
		WireButton("MarginContainer/VBoxContainer/QuitButton", OnQuit);
	}

	private void WireButton(string path, System.Action handler)
	{
		var btn = GetNodeOrNull<Button>(path);
		if (btn == null)
		{
			GD.PushError($"[MainMenu] Button not found at path: \"{path}\". Check node names/hierarchy in MainMenu.tscn.");
			return;
		}
		btn.Pressed += () => handler?.Invoke();
	}

	private void OnPlay()
	{
		var save = GetNodeOrNull<SaveManager>("/root/SaveManager");
		if (save == null)
		{
			GD.PushWarning("[MainMenu] SaveManager autoload not found. Loading default Level1.");
			GetTree().ChangeSceneToFile("res://Levels/level1.tscn");
			return;
		}

		int max = save.GetMaxUnlocked();
		string path = save.GetLevelPath(max);
		var err = GetTree().ChangeSceneToFile(path);
		if (err != Error.Ok)
			GD.PushError($"[MainMenu] Failed to load {path}. Does it exist?");
	}

	private void OnLevelSelect()
	{
		var path = "res://UI/LevelSelect.tscn";
		var err = GetTree().ChangeSceneToFile(path);
		if (err != Error.Ok)
			GD.PushError($"[MainMenu] Cannot open file '{path}'. Create it or fix the path.");
	}

	private void OnQuit() => GetTree().Quit();
}
