using Godot;

public partial class Player : CharacterBody2D
{
	[Export] public float MoveSpeed = 220f;
	[Export] public float JumpVelocity = 350f;
	[Export] public float _gravity = 900f;

	public override void _Ready()
	{
		_gravity = (float)ProjectSettings.GetSetting("physics/2d/default_gravity");
		GD.Print("Player _Ready OK");
	}

	public override void _PhysicsProcess(double delta)
	{
		// Prove _PhysicsProcess runs
		// (Comment this after confirming to avoid spam)
		// GD.Print("tick");

		var v = Velocity;
		float dt = (float)delta;

		// Gravity
		if (!IsOnFloor())
			v.Y += _gravity * dt;

		// Use built-in actions to avoid InputMap issues
		float input = Input.GetAxis("ui_left", "ui_right");
		if (Mathf.Abs(input) > 0.01f)
			v.X = input * MoveSpeed;
		else
			v.X = IsOnFloor() ? Mathf.MoveToward(v.X, 0, 2000f * dt) : v.X;

		// Jump
		if (IsOnFloor() && Input.IsActionJustPressed("ui_up"))
			v.Y = -JumpVelocity;

		Velocity = v;
		MoveAndSlide();

		// Debug prints (temporarily)
		// GD.Print($"input={input}, onFloor={IsOnFloor()}, v=({Velocity.X:0.0},{Velocity.Y:0.0})");
	}
}
