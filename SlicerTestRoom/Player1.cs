using Godot;
using System;

/// <summary>
/// Player1: A fully-featured first-person player controller for 3D platforming / FPS movement.
/// Handles walking, sprinting, jumping, mouselook (yaw on body, pitch on camera),
/// jump buffering, coyote time, and smooth acceleration.
/// </summary>
public partial class Player1 : CharacterBody3D
{
	
	//slice return variable
	private bool _justReturnedFromSlice = false;

	
	// ===============================
	// ==== CAMERA CONFIGURATION ====
	// ===============================

	[ExportGroup("Camera")]
	[Export] public NodePath CameraPath;         // Reference to the Camera3D node (child of this player)
	[Export] public float MouseSensitivity = 0.12f; // Mouse look sensitivity in degrees per pixel
	[Export] public bool InvertY = false;           // If true, vertical look is inverted (flight sim style)
	[Export] public float MinPitch = -80f;          // Minimum pitch angle (looking up limit)
	[Export] public float MaxPitch = 80f;           // Maximum pitch angle (looking down limit)

	// ===============================
	// ===== MOVEMENT SETTINGS ======
	// ===============================

	[ExportGroup("Movement")]
	[Export] public float WalkSpeed = 6.0f;         // Base walking speed
	[Export] public float SprintSpeed = 9.5f;       // Speed while sprinting
	[Export] public float GroundAcceleration = 18.0f; // How quickly you accelerate/decelerate on the ground
	[Export] public float AirAcceleration = 6.0f;   // Slower acceleration in the air
	[Export] public float MaxSlopeAngleDegrees = 46f; // Max climbable slope angle

	// ===============================
	// ======= JUMP SETTINGS ========
	// ===============================

	[ExportGroup("Jumping")]
	[Export] public float JumpVelocity = 5.8f;      // Initial upward velocity when jumping
	[Export] public int MaxAirJumps = 0;            // Number of extra jumps allowed in air (0 = no double jump)
	[Export] public float CoyoteTime = 0.12f;       // Small grace period after walking off a ledge
	[Export] public float JumpBuffer = 0.12f;       // Buffer time for pressing jump just before landing

// ----- Freeze Flag --------
[Export] public bool ControlsEnabled = true;


	// ===============================
	// ===== INTERNAL VARIABLES =====
	// ===============================

	private Camera3D _cam;                           // Cached camera reference
	private float _pitchDeg = 0f;                    // Current camera pitch in degrees (+X = look down)
	private float _yawDeg = 0f;                      // Current player yaw in degrees (body rotation)
	private float _gravity;                          // Pulled from project settings
	private float _coyoteTimer;                      // Countdown for coyote time grace window
	private float _jumpBufferTimer;                  // Countdown for jump buffer input window
	private int _airJumpsLeft;                       // How many mid-air jumps remain

	// ===============================
	// ===== INITIALIZATION =========
	// ===============================

	public override void _Ready()
	{
		// --- CAMERA SETUP ---
		_cam = GetNodeOrNull<Camera3D>(CameraPath);
		if (_cam != null)
		{
			_cam.Current = true;            // Make this the active camera
			_cam.RotationDegrees = Vector3.Zero; // Start with neutral orientation
			_cam.Scale = Vector3.One;            // Ensure no inherited negative scale (avoids flipped camera)
		}

		// --- PLAYER TRANSFORM CLEANUP ---
		// The player only rotates around the Y-axis (yaw).
		// This prevents pitch/roll from sneaking in if the editor transform isn’t reset.
		RotationDegrees = new Vector3(0f, RotationDegrees.Y, 0f);

		// --- INPUT SETUP ---
		// Capture and hide the mouse cursor (FPS-style)
		Input.MouseMode = Input.MouseModeEnum.Captured;

		// --- PHYSICS & DEFAULT VALUES ---
		// Pull gravity from project settings so it matches the global environment
		_gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();
		Velocity = Vector3.Zero;                        // Start still
		FloorMaxAngle = Mathf.DegToRad(MaxSlopeAngleDegrees); // Convert slope limit to radians
		FloorStopOnSlope = true;                        // Prevent sliding down gentle slopes
		FloorSnapLength = 0.2f;                         // Keeps player “stuck” to the ground when walking off small ledges

		_airJumpsLeft = MaxAirJumps;                    // Initialize available air jumps

		// --- OPTIONAL: ADD SPRINT ACTION ---
		// If "sprint" doesn't exist in Input Map, create it (avoids null-reference errors)
		if (!InputMap.HasAction("sprint"))
			InputMap.AddAction("sprint");
	}

	// ===============================
	// ===== INPUT (MOUSE LOOK) =====
	// ===============================

	public override void _Input(InputEvent e)
	{
		// Handle mouse movement for rotation
		if (e is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			// --- YAW (Horizontal Look) ---
			// Rotate the player body around Y axis
			// Negative X for standard FPS direction (mouse right = turn right)
			_yawDeg += -m.Relative.X * MouseSensitivity;
			RotationDegrees = new Vector3(0f, _yawDeg, 0f);

			// --- PITCH (Vertical Look) ---
			// Mouse up/down moves camera pitch.
			// Convention here: +X = look down, -X = look up.
			float dy = m.Relative.Y * MouseSensitivity * (InvertY ? 1f : -1f);
			_pitchDeg = Mathf.Clamp(_pitchDeg + dy, MinPitch, MaxPitch);

			// Apply rotation only on X to the camera (no roll, no local yaw)
			if (_cam != null)
				_cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
		}

		// --- ESC KEY ---
		// Releases the mouse so the player can interact with UI or exit.
		if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
			Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	// ===============================
	// ===== PHYSICS MOVEMENT =======
	// ===============================

	public override void _PhysicsProcess(double delta)
	{
		
		if (!ControlsEnabled)
{
	// While frozen, don't integrate movement at all; keep a tiny
	// downward bias only if you want them to re-stick to floor
	// when unfreezing (optional). Usually full zero is safer.
	// Velocity = Vector3.Zero;
	return;
}

if (HasMeta("justReturnedFromSlice") && (bool)GetMeta("justReturnedFromSlice"))
{
	SetMeta("justReturnedFromSlice", false);
	return; // skip this frame to prevent gravity tick
}


		
		float dt = (float)delta;

		// --- UPDATE TIMERS ---
		if (_coyoteTimer > 0f) _coyoteTimer -= dt;
		if (_jumpBufferTimer > 0f) _jumpBufferTimer -= dt;

		// --- GRAVITY & FLOOR STATE ---
		if (IsOnFloor())
		{
			// When grounded, reset coyote timer and air jumps
			_coyoteTimer = CoyoteTime;
			_airJumpsLeft = MaxAirJumps;

			// Clear any slight downward velocity to “stick” to ground
			if (Velocity.Y < 0f)
				Velocity = new Vector3(Velocity.X, -0.1f, Velocity.Z);
		}
		else
		{
			// Apply gravity when airborne
			Velocity += Vector3.Down * _gravity * dt;
		}

		// --- MOVEMENT INPUT ---
		// Get WASD or arrow key input from the InputMap
		Vector2 input2D = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");

		// Sprint if key is held and action exists
		bool wantsSprint = InputMap.HasAction("sprint") && Input.IsActionPressed("sprint");
		float targetSpeed = wantsSprint ? SprintSpeed : WalkSpeed;

		// --- MOVE DIRECTION (RELATIVE TO PLAYER YAW) ---
		// Use the player’s rotated basis vectors to find forward/right directions.
		Basis b = GlobalTransform.Basis;

		// Project basis vectors onto the XZ plane to ignore any camera pitch
		Vector3 forward = (-b.Z - Vector3.Up * (-b.Z).Dot(Vector3.Up)).Normalized();
		Vector3 right   = ( b.X - Vector3.Up *  b.X .Dot(Vector3.Up)).Normalized();

		// Combine horizontal inputs with forward/right directions
		// Note: negative input2D.Y because GetVector’s up is -1
		Vector3 moveDir = (right * input2D.X + forward * (-input2D.Y));
		if (moveDir.Length() > 1f)
			moveDir = moveDir.Normalized(); // Avoid diagonal speed boost

		// --- ACCELERATION / DECELERATION ---
		Vector3 horizVel = new Vector3(Velocity.X, 0f, Velocity.Z); // current horizontal velocity
		Vector3 targetHoriz = moveDir * targetSpeed;                // desired velocity

		// Choose acceleration rate depending on grounded state
		float accel = IsOnFloor() ? GroundAcceleration : AirAcceleration;

		// Smoothly approach the target horizontal velocity
		if (IsOnFloor() && moveDir == Vector3.Zero)
			horizVel = horizVel.MoveToward(Vector3.Zero, GroundAcceleration * dt); // brake when no input
		else
			horizVel = horizVel.MoveToward(targetHoriz, accel * dt);

		// Apply new velocity
		Velocity = new Vector3(horizVel.X, Velocity.Y, horizVel.Z);

		// --- JUMP HANDLING ---
		// Buffer jump input so pressing just before landing still triggers
		if (Input.IsActionJustPressed("ui_accept"))
			_jumpBufferTimer = JumpBuffer;

		// Conditions for allowed jumps
		bool canGroundJump = _coyoteTimer > 0f;
		bool canAirJump = !IsOnFloor() && _coyoteTimer <= 0f && _airJumpsLeft > 0;

		// Execute jump if buffered and possible
		if (_jumpBufferTimer > 0f && (canGroundJump || canAirJump))
		{
			_jumpBufferTimer = 0f;    // consume buffer
			float vy = Velocity.Y;
			if (vy < 0f) vy = 0f;     // reset downward velocity before jumping

			Velocity = new Vector3(Velocity.X, vy + JumpVelocity, Velocity.Z);

			// Decrement air jump if it was an air jump
			if (canAirJump && !canGroundJump)
				_airJumpsLeft--;

			_coyoteTimer = 0f;        // consume coyote time
		}

		// --- VARIABLE JUMP HEIGHT ---
		// Release the jump key early → cut upward velocity for shorter hops
		if (Input.IsActionJustReleased("ui_accept") && Velocity.Y > 0f)
			Velocity = new Vector3(Velocity.X, Velocity.Y * 0.5f, Velocity.Z);

		// --- APPLY MOVEMENT ---
		// Move the player and handle collisions/ground detection automatically
		MoveAndSlide();
	}
	
	public void FreezeMotion(bool frozen)
{
	ControlsEnabled = !frozen;
	// kill momentum immediately
	Velocity = Vector3.Zero;
}
	
}
