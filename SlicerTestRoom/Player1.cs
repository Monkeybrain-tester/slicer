using Godot;
using System;

/// <summary>
/// Player1: First-person character controller with smooth acceleration,
/// slope-friendly grounding (including capsule bottom edges),
/// jump buffering, and coyote time.
/// Integrates with SliceManager via ControlsEnabled + "justReturnedFromSlice" metadata.
/// </summary>
public partial class Player1 : CharacterBody3D
{
	// slice return variable (kept for compatibility, though we use metadata)
	private bool _justReturnedFromSlice = false;

	// ===============================
	// ==== CAMERA CONFIGURATION ====
	// ===============================

	[ExportGroup("Camera")]
	[Export] public NodePath CameraPath;
	[Export] public float MouseSensitivity = 0.12f;
	[Export] public bool InvertY = false;
	[Export] public float MinPitch = -80f;
	[Export] public float MaxPitch = 80f;

	// ===============================
	// ===== MOVEMENT SETTINGS ======
	// ===============================

	[ExportGroup("Movement")]
	[Export] public float WalkSpeed = 6.0f;
	[Export] public float SprintSpeed = 9.5f;
	[Export] public float GroundAcceleration = 20.0f;   // how fast we accelerate on ground
	[Export] public float AirAcceleration = 8.0f;       // how strongly we “pull” toward input in air
	[Export] public float GroundFriction = 8.0f;        // how fast we stop when no input
	[Export] public float AirControl = 0.4f;            // 0–1: steering control in air
	[Export] public float MaxSlopeAngleDegrees = 55f;   // slope angle to still treat as ground
	[Export] public float MaxFallSpeed = 60f;

	// ===============================
	// ======= JUMP SETTINGS ========
	// ===============================

	[ExportGroup("Jumping")]
	[Export] public float JumpVelocity = 6.5f;
	[Export] public int MaxAirJumps = 0;
	[Export] public float CoyoteTime = 0.12f;
	[Export] public float JumpBuffer = 0.12f;

	// ===============================
	// ======= SLICE / CONTROL ======
	// ===============================

	[ExportGroup("Control")]
	[Export] public bool ControlsEnabled = true;

	// ===============================
	// ===== INTERNAL VARIABLES =====
	// ===============================

	private Camera3D _cam;
	private float _pitchDeg = 0f;
	private float _yawDeg = 0f;

	private float _gravity;
	private float _coyoteTimer;
	private float _jumpBufferTimer;
	private int _airJumpsLeft;

	private bool _pitchLocked = false;
	private float _savedPitchDeg = 0f;

	// Custom grounded state (includes capsule bottom edges)
	private bool _isGrounded = false;
	private Vector3 _groundNormal = Vector3.Up;
	private bool _wasGroundedPrev = false;

	// ===============================
	// ========= INITIALIZE =========
	// ===============================

	public override void _Ready()
	{
		// Camera
		_cam = GetNodeOrNull<Camera3D>(CameraPath);
		if (_cam != null)
		{
			_cam.Current = true;
			_cam.RotationDegrees = Vector3.Zero;
			_cam.Scale = Vector3.One;
		}

		// Only yaw on the body
		RotationDegrees = new Vector3(0f, RotationDegrees.Y, 0f);

		Input.MouseMode = Input.MouseModeEnum.Captured;

		// Physics
		_gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();
		Velocity = Vector3.Zero;

		FloorMaxAngle = Mathf.DegToRad(MaxSlopeAngleDegrees);
		FloorStopOnSlope = true;
		FloorSnapLength = 0.6f; // slightly longer to help with small steps / lips

		_airJumpsLeft = MaxAirJumps;

		if (!InputMap.HasAction("sprint"))
			InputMap.AddAction("sprint");
	}

	// ===============================
	// ===== INPUT (MOUSE LOOK) =====
	// ===============================

	public override void _Input(InputEvent e)
	{
		if (e is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			// YAW
			_yawDeg += -m.Relative.X * MouseSensitivity;
			RotationDegrees = new Vector3(0f, _yawDeg, 0f);

			// PITCH
			if (!_pitchLocked)
			{
				float dy = m.Relative.Y * MouseSensitivity * (InvertY ? 1f : -1f);
				_pitchDeg = Mathf.Clamp(_pitchDeg + dy, MinPitch, MaxPitch);
			}
			else
			{
				_pitchDeg = 0f;
			}

			if (_cam != null)
				_cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
		}
		//This WOULD unlock the camera on escape, I'm using the escape key for a level menu now
		//if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
			//Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	// ===============================
	// ===== PHYSICS MOVEMENT =======
	// ===============================

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;

		if (!ControlsEnabled)
{
	// allow an automatic unfreeze if we detect grounded again
	if (IsOnFloor())
		ControlsEnabled = true;
	else
		return;
}

if (HasMeta("justReturnedFromSlice") && (bool)GetMeta("justReturnedFromSlice"))
{
	GD.Print("[Player1] Returning from Slice — skipping one frame.");
	SetMeta("justReturnedFromSlice", false);
	return;
}


		// Use our custom grounded state from the *previous* frame
		bool onFloor = _isGrounded;

		// Timers
		if (_coyoteTimer > 0f) _coyoteTimer -= dt;
		if (_jumpBufferTimer > 0f) _jumpBufferTimer -= dt;

		// ===== VERTICAL / GRAVITY =====
		if (onFloor)
		{
			if (!_wasGroundedPrev)
			{
				// Just landed
				_coyoteTimer = CoyoteTime;
				_airJumpsLeft = MaxAirJumps;
			}
			else
			{
				_coyoteTimer = CoyoteTime;
			}

			if (Velocity.Y < 0f)
				Velocity = new Vector3(Velocity.X, -0.2f, Velocity.Z);
		}
		else
		{
			float vy = Velocity.Y - _gravity * dt;
			if (vy < -MaxFallSpeed)
				vy = -MaxFallSpeed;
			Velocity = new Vector3(Velocity.X, vy, Velocity.Z);
		}

		_wasGroundedPrev = onFloor;

		// ===== HORIZONTAL MOVEMENT =====

		Vector2 moveInput = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
		bool wantsSprint = InputMap.HasAction("sprint") && Input.IsActionPressed("sprint");
		float targetSpeed = wantsSprint ? SprintSpeed : WalkSpeed;

		Basis b = GlobalTransform.Basis;
		Vector3 forward = -b.Z;
		forward.Y = 0f;
		forward = forward.Normalized();

		Vector3 right = b.X;
		right.Y = 0f;
		right = right.Normalized();

		Vector3 wishDir = (right * moveInput.X + forward * (-moveInput.Y));
		if (wishDir.LengthSquared() > 1e-6f)
			wishDir = wishDir.Normalized();

		Vector3 horizVel = new Vector3(Velocity.X, 0f, Velocity.Z);
		Vector3 targetVel = wishDir * targetSpeed;

		// Project target velocity onto ground plane when grounded
		Vector3 groundN = _isGrounded ? _groundNormal : Vector3.Up;
		groundN = groundN.LengthSquared() > 0.0001f ? groundN.Normalized() : Vector3.Up;

		if (onFloor && wishDir.LengthSquared() > 1e-6f)
		{
			targetVel -= groundN * targetVel.Dot(groundN);
		}

		if (onFloor)
		{
			if (wishDir.LengthSquared() < 1e-4f)
			{
				// no input → friction
				float frictionStep = GroundFriction * dt;
				horizVel = horizVel.MoveToward(Vector3.Zero, frictionStep);
			}
			else
			{
				float accelStep = GroundAcceleration * dt;
				horizVel = horizVel.MoveToward(targetVel, accelStep);
			}
		}
		else
		{
			// Air control using projection of velocity along wished direction
			if (wishDir.LengthSquared() > 1e-6f)
			{
				Vector3 wish = wishDir * targetSpeed;
				Vector3 wishNorm = wish.Normalized();
				float currentAlong = horizVel.Dot(wishNorm);
				float targetAlong = wish.Length();
				float maxChange = AirAcceleration * dt * AirControl;

				float newAlong = Mathf.MoveToward(currentAlong, targetAlong, maxChange);
				horizVel += wishNorm * (newAlong - currentAlong);
			}
		}

		Velocity = new Vector3(horizVel.X, Velocity.Y, horizVel.Z);

		// ===== JUMPING =====

		if (Input.IsActionJustPressed("ui_accept"))
			_jumpBufferTimer = JumpBuffer;

		bool canGroundJump = _coyoteTimer > 0f;
		bool canAirJump = !onFloor && _coyoteTimer <= 0f && _airJumpsLeft > 0;

		if (_jumpBufferTimer > 0f && (canGroundJump || canAirJump))
		{
			_jumpBufferTimer = 0f;

			float vy = Velocity.Y;
			if (vy < 0f) vy = 0f;
			vy += JumpVelocity;

			Velocity = new Vector3(Velocity.X, vy, Velocity.Z);

			if (canAirJump && !canGroundJump)
				_airJumpsLeft--;

			_coyoteTimer = 0f;
		}

		if (Input.IsActionJustReleased("ui_accept") && Velocity.Y > 0f)
			Velocity = new Vector3(Velocity.X, Velocity.Y * 0.5f, Velocity.Z);

		// ===== MOVE & UPDATE GROUND STATE =====

		MoveAndSlide();
		UpdateGroundedFromCollisions();
	}

	/// <summary>
	/// Custom grounded detection: treat ANY collision whose normal is within
	/// MaxSlopeAngleDegrees of Up as "ground", including capsule bottom edges.
	/// </summary>
	private void UpdateGroundedFromCollisions()
	{
		_isGrounded = false;
		_groundNormal = Vector3.Up;

		float maxAngleRad = Mathf.DegToRad(MaxSlopeAngleDegrees);
		float cosMax = Mathf.Cos(maxAngleRad);

		float bestDot = 0f;
		Vector3 bestNormal = Vector3.Up;

		// Also include engine's floor classification if available
		if (IsOnFloor())
		{
			Vector3 fn = GetFloorNormal();
			fn = fn.LengthSquared() > 0.0001f ? fn.Normalized() : Vector3.Up;
			float dot = fn.Dot(Vector3.Up);
			if (dot > bestDot)
			{
				bestDot = dot;
				bestNormal = fn;
			}
		}

		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			var col = GetSlideCollision(i);
			Vector3 n = col.GetNormal().Normalized();
			float dot = n.Dot(Vector3.Up);

			// dot >= cosMax → angle between normal & Up <= MaxSlopeAngleDegrees
			if (dot >= cosMax && dot > bestDot)
			{
				bestDot = dot;
				bestNormal = n;
			}
		}

		if (bestDot >= cosMax)
		{
			_isGrounded = true;
			_groundNormal = bestNormal;
		}
	}

	// ===============================
	// ===== SLICE / UTILITY API ====
	// ===============================

	public void FreezeMotion(bool frozen)
	{
		ControlsEnabled = !frozen;
		Velocity = Vector3.Zero;
	}

	public void LockPitch(bool snapToHorizon = true)
	{
		_pitchLocked = true;
		if (snapToHorizon)
		{
			_pitchDeg = 0f;
			if (_cam != null)
				_cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
		}
	}

	public void UnlockPitch()
	{
		_pitchLocked = false;
		if (_cam != null)
			_cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
	}

	public void LockPitchToHorizon()
	{
		_pitchLocked = true;
		_savedPitchDeg = 0f;
		_pitchDeg = 0f;

		if (_cam != null)
			_cam.RotationDegrees = new Vector3(0f, 0f, 0f);
	}
}
