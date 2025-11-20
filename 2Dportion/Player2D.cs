using Godot;

public partial class Player2D : CharacterBody2D
{
	[Export] public float MoveSpeed = 10;
	[Export] public float JumpSpeed = 10;
	[Export] public float Gravity = 1060;
	
	

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;
		var v = Velocity;

		v.Y += Gravity * dt;

		float x = 0f;
		if (Input.IsActionPressed("ui_left"))  x -= 1f;
		if (Input.IsActionPressed("ui_right")) x += 1f;
		v.X = x * MoveSpeed;

		if (IsOnFloor() && Input.IsActionJustPressed("ui_accept"))
			v.Y = -JumpSpeed;

		Velocity = v;
		MoveAndSlide();
	}
}
