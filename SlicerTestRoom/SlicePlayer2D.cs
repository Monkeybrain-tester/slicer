using Godot;

public partial class SlicePlayer2D : CharacterBody2D
{
	[Export] public float MoveSpeed = 200f;
	[Export] public float JumpVelocity = -320f;
	[Export] public float Gravity = 900f;

	public Vector2 StartPos;   // set by SliceManager
	public Vector2 Displacement => GlobalPosition - StartPos;

	public override void _Ready()
	{
		GlobalPosition = StartPos;
	}

	public override void _PhysicsProcess(double delta)
	{
		var v = Velocity;
		v.Y += Gravity * (float)delta;

		float input = Input.GetActionStrength("ui_right") - Input.GetActionStrength("ui_left");
		v.X = input * MoveSpeed;

		if (IsOnFloor() && (Input.IsActionJustPressed("ui_accept") || Input.IsActionJustPressed("ui_up")))
			v.Y = JumpVelocity;

		Velocity = v;
		MoveAndSlide();
	}
}
