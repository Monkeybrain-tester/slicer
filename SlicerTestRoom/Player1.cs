using Godot;
using System;

public partial class Player1 : CharacterBody3D
{
	[ExportGroup("Camera")]
	[Export] public NodePath CameraPath;
	[Export] public float MouseSensitivity = 0.12f; // deg per pixel
	[Export] public bool InvertY = false;           // flight-sim style if true
	[Export] public float MinPitch = -80f;
	[Export] public float MaxPitch = 80f;

	[ExportGroup("Movement")]
	[Export] public float WalkSpeed = 6.0f;
	[Export] public float SprintSpeed = 9.5f;
	[Export] public float GroundAcceleration = 18.0f;
	[Export] public float AirAcceleration = 6.0f;
	[Export] public float MaxSlopeAngleDegrees = 46f;

	[ExportGroup("Jumping")]
	[Export] public float JumpVelocity = 5.8f;
	[Export] public int MaxAirJumps = 0;             // 0 = no double jump
	[Export] public float CoyoteTime = 0.12f;
	[Export] public float JumpBuffer = 0.12f;

	private Camera3D _cam;
	private float _pitchDeg = 0f;                    // camera pitch (degrees). +X = look down, -X = look up
	private float _yawDeg = 0f;                      // player yaw  (degrees)
	private float _gravity;
	private float _coyoteTimer;
	private float _jumpBufferTimer;
	private int _airJumpsLeft;

	public override void _Ready()
	{
		// --- Camera setup ---
		_cam = GetNodeOrNull<Camera3D>(CameraPath);
		if (_cam != null)
		{
			_cam.Current = true;

			// Force a clean baseline so we can't be upside-down from inherited transforms.
			_cam.RotationDegrees = Vector3.Zero;   // no pitch/yaw/roll yet
			_cam.Scale = Vector3.One;              // kill any negative scale flips
		}

		// Player only yaws; ensure no stray pitch/roll on the body.
		RotationDegrees = new Vector3(0f, RotationDegrees.Y, 0f);

		// Capture the mouse
		Input.MouseMode = Input.MouseModeEnum.Captured;

		// Physics defaults
		_gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();
		Velocity = Vector3.Zero;
		FloorMaxAngle = Mathf.DegToRad(MaxSlopeAngleDegrees);
		FloorStopOnSlope = true;
		FloorSnapLength = 0.2f;

		_airJumpsLeft = MaxAirJumps;

		// Optional: make sprint action safe if not present
		if (!InputMap.HasAction("sprint"))
			InputMap.AddAction("sprint");
	}

	public override void _Input(InputEvent e)
	{
		if (e is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			// Yaw: rotate the player around Y (left/right)
			_yawDeg += -m.Relative.X * MouseSensitivity; // negative = typical FPS feel
			RotationDegrees = new Vector3(0f, _yawDeg, 0f);

			// Pitch on the camera (up/down). Your convention: +X = down, -X = up
			float dy = m.Relative.Y * MouseSensitivity * (InvertY ? 1f : -1f);
			_pitchDeg = Mathf.Clamp(_pitchDeg + dy, MinPitch, MaxPitch);

			if (_cam != null)
			{
				// Apply pitch ONLY on X and lock roll (Z) and yaw (Y) at camera level
				_cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
				// If you're ever worried about inherited roll, uncomment next line:
				// _cam.Rotation = new Vector3(Mathf.DegToRad(_pitchDeg), 0f, 0f);
			}
		}

		// Esc to release mouse
		if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
			Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;

		// Timers
		if (_coyoteTimer > 0f) _coyoteTimer -= dt;
		if (_jumpBufferTimer > 0f) _jumpBufferTimer -= dt;

		// Grounding & gravity
		if (IsOnFloor())
		{
			_coyoteTimer = CoyoteTime;
			_airJumpsLeft = MaxAirJumps;
			if (Velocity.Y < 0f) Velocity = new Vector3(Velocity.X, -0.1f, Velocity.Z);
		}
		else
		{
			Velocity += Vector3.Down * _gravity * dt;
		}

		// Movement input (camera-independent; uses player yaw)
		Vector2 input2D = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
		bool wantsSprint = InputMap.HasAction("sprint") && Input.IsActionPressed("sprint");
		float targetSpeed = wantsSprint ? SprintSpeed : WalkSpeed;

		// Basis from the player (yawed). Build XZ move dir: right * x + forward * y
		Basis b = GlobalTransform.Basis;
		Vector3 forward = (-b.Z - Vector3.Up * (-b.Z).Dot(Vector3.Up)).Normalized();
		Vector3 right   = ( b.X - Vector3.Up *  b.X .Dot(Vector3.Up)).Normalized();
		Vector3 moveDir = (right * input2D.X + forward * (-input2D.Y));
		if (moveDir.Length() > 1f) moveDir = moveDir.Normalized();

		Vector3 horizVel = new Vector3(Velocity.X, 0f, Velocity.Z);
		Vector3 targetHoriz = moveDir * targetSpeed;
		float accel = IsOnFloor() ? GroundAcceleration : AirAcceleration;

		// Accelerate/brake on the horizontal plane (no auto-facing)
		if (IsOnFloor() && moveDir == Vector3.Zero)
			horizVel = horizVel.MoveToward(Vector3.Zero, GroundAcceleration * dt);
		else
			horizVel = horizVel.MoveToward(targetHoriz, accel * dt);

		Velocity = new Vector3(horizVel.X, Velocity.Y, horizVel.Z);

		// Jump buffer + coyote
		if (Input.IsActionJustPressed("ui_accept"))
			_jumpBufferTimer = JumpBuffer;

		bool canGroundJump = _coyoteTimer > 0f;
		bool canAirJump = !IsOnFloor() && _coyoteTimer <= 0f && _airJumpsLeft > 0;

		if (_jumpBufferTimer > 0f && (canGroundJump || canAirJump))
		{
			_jumpBufferTimer = 0f;
			float vy = Velocity.Y;
			if (vy < 0f) vy = 0f;
			Velocity = new Vector3(Velocity.X, vy + JumpVelocity, Velocity.Z);

			if (canAirJump && !canGroundJump) _airJumpsLeft--;
			_coyoteTimer = 0f;
		}

		// Variable jump height
		if (Input.IsActionJustReleased("ui_accept") && Velocity.Y > 0f)
			Velocity = new Vector3(Velocity.X, Velocity.Y * 0.5f, Velocity.Z);

		MoveAndSlide();
	}
}
