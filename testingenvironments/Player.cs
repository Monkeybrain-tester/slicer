using System.ComponentModel;
using Godot;


public partial class Player : CharacterBody2D
{
	[Export] public float MoveSpeed = 220f;
	[Export] public float JumpVelocity = 350f;
	[Export] public float _gravity = 2000f;
	
	// Possible States
	enum State { Idle, Run }
	private State current_state;

    

    public override void _Ready()
	{
		current_state = State.Idle;
	}

	public override void _PhysicsProcess(double delta)
	{
		player_falling(delta);
		player_idle(delta);

		MoveAndSlide();
	}

	// Falling state
	public void player_falling(double delta)
	{
		if (!IsOnFloor())
		{
			Velocity += new Vector2(0, _gravity * (float)delta);
		}
	}
	
	public void player_idle(double delta)
	{
        if (IsOnFloor())
		{
			current_state = State.Idle;
		}
    }
}
