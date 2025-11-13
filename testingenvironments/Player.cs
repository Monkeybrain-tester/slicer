using System.ComponentModel;
using System.Threading.Tasks;
using Godot;


public partial class Player : CharacterBody2D
{
	[Export] public float MoveSpeed = 220f;
	[Export] public float JumpVelocity = 350f;
	[Export] public float _gravity = 2000f;
	[Export] public float Acceleration = 500f;
	[Export] public float LowJumpMultiplier = 0.5f;
	[Export] public float Friction = 0.2f;
	[Export] public float JumpBufferTime = 0.1f;
	
	private AnimatedSprite2D _animatedSprite2D;
	private Timer _coyoteTimer;
	private Timer _jumpBufferTimer;

	// Possible States
	enum State { Idle, Run, Jump, Skid, Land }
	private State current_state;

    public override void _Ready()
	{
		// Assigns Player Child Nodes to vairables
		_animatedSprite2D = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_coyoteTimer = GetNode<Timer>("CoyoteTimer");
		_jumpBufferTimer = GetNode<Timer>("JumpBufferTimer");
		
		// Sets initial state to Idle
		current_state = State.Idle;
	}

	public override void _PhysicsProcess(double delta)
	{
		// Detects state
		player_falling(delta);
		player_idle(delta);
		player_run(delta);
		player_jump(delta);

		// Handles Coyote Time
		var was_on_floor = IsOnFloor();
		MoveAndSlide();
		if (was_on_floor && !IsOnFloor())
			_coyoteTimer.Start();

		// Handles animations based on state
		player_animation();
	}

	// Falling state
	public void player_falling(double delta)
	{
		if (!IsOnFloor())
		{
			current_state = State.Jump;
			Velocity += new Vector2(0, _gravity * (float)delta);
		}
	}

    // Idle state
	public void player_idle(double delta)
	{
		if (IsOnFloor())
			current_state = State.Idle;
	}

	// Run state
	public void player_run(double delta)
	{
		var direction = Input.GetAxis("move_left", "move_right");
		float target_speed = direction * MoveSpeed;

		// Move in pressed direction & set run state
		if (direction != 0.0f)
        {
			Velocity = new Vector2(Mathf.MoveToward(Velocity.X, target_speed, Acceleration * (float)delta), Velocity.Y);
			current_state = State.Run;

			// Flips sprite when moving opposite direction
			if (direction > 0.0f) _animatedSprite2D.FlipH = false;
			else _animatedSprite2D.FlipH = true;
        }

		// Player slides back to 0 velocity
		else
        {
            if(!IsOnFloor())
				Velocity = new Vector2(Mathf.MoveToward(Velocity.X, 0, Friction * (float)delta * 2), Velocity.Y);
			else
				Velocity = new Vector2(Mathf.MoveToward(Velocity.X, 0, Friction * (float)delta), Velocity.Y);
        }
			
	}
	
	// Jump state
	public void player_jump(double delta)
    {
        // Initial jump
		if ((Input.IsActionJustPressed("jump") && (IsOnFloor() || !_coyoteTimer.IsStopped())) || (IsOnFloor() && !_jumpBufferTimer.IsStopped()))
		{
			Velocity = new Vector2(Velocity.X, JumpVelocity * -1);
			_jumpBufferTimer.Stop();
		}

		// Short hop
		if (!Input.IsActionPressed("jump") && Velocity.Y < 0)
        	// Apply low jump multiplier when jump is released
        	Velocity = new Vector2(Velocity.X, Velocity.Y * 0.5f); 

		// Jump buffer
		if (Input.IsActionJustPressed("jump") && !IsOnFloor())
			_jumpBufferTimer.Start();

    }

	// Handles animation based on state
	public async Task player_animation()
    {
		if (current_state == State.Idle)
			_animatedSprite2D.Play("idle");
		else if (current_state == State.Run)
			_animatedSprite2D.Play("running");
    }

}
