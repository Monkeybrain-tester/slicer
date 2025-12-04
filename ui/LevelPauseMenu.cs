using Godot;
using System;

public partial class LevelPauseMenu : Control
{
	[Export] public NodePath PlayerPath;  // drag your Player1 here in each level
	[Export] public string MainMenuScenePath = "res://UI/MainMenu.tscn";

	private Player1 _player;
	private bool _isOpen = false;

	public override void _Ready()
	{
		// So this menu still processes input even if you later decide to pause the tree
		ProcessMode = ProcessModeEnum.Always;

		_player = GetNodeOrNull<Player1>(PlayerPath);

		// Wire buttons (make sure the paths match your scene hierarchy)
		var resumeBtn  = GetNode<Button>("Panel/VBoxContainer/ResumeButton");
		var restartBtn = GetNode<Button>("Panel/VBoxContainer/RestartButton");
		var mainBtn    = GetNode<Button>("Panel/VBoxContainer/MainMenuButton");
		var quitBtn    = GetNode<Button>("Panel/VBoxContainer/QuitButton");

		resumeBtn.Pressed  += OnResumePressed;
		restartBtn.Pressed += OnRestartPressed;
		mainBtn.Pressed    += OnMainMenuPressed;
		quitBtn.Pressed    += OnQuitPressed;

		Visible = false;
		SetProcessUnhandledInput(true);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
		{
			TogglePause();
			GetViewport().SetInputAsHandled();
		}
	}

	private void TogglePause()
	{
		if (_isOpen)
			CloseMenu();
		else
			OpenMenu();
	}

	private void OpenMenu()
	{
		_isOpen = true;
		Visible = true;

		if (_player != null)
			_player.FreezeMotion(true);

		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	private void CloseMenu()
	{
		_isOpen = false;
		Visible = false;

		if (_player != null)
			_player.FreezeMotion(false);

		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void OnResumePressed() => CloseMenu();

	private void OnRestartPressed()
	{
		if (_player != null)
			_player.FreezeMotion(false);

		Input.MouseMode = Input.MouseModeEnum.Captured;
		GetTree().ReloadCurrentScene();
	}

	private void OnMainMenuPressed()
	{
		if (_player != null)
			_player.FreezeMotion(false);

		Input.MouseMode = Input.MouseModeEnum.Visible;

		var err = GetTree().ChangeSceneToFile(MainMenuScenePath);
		if (err != Error.Ok)
			GD.PushError($"[LevelPauseMenu] Failed to load {MainMenuScenePath}");
	}

	private void OnQuitPressed()
	{
		GetTree().Quit();
	}
}
