# ProtoController v1.0 by Brackeys
# CC0 License

extends CharacterBody3D

@onready var camera_main : Camera3D = $Head/Camera3D
@onready var camera_side : Camera3D = $Head/Camera3D2
@onready var mesh_3d : MeshInstance3D = $MeshInstance3D           # Path to your 3D mesh
@onready var mesh_2d : MeshInstance2D = $MeshInstance2D           # Path to your 2D mesh

@export var can_move : bool = true
@export var has_gravity : bool = true
@export var can_jump : bool = true
@export var can_sprint : bool = false
@export var can_freefly : bool = false

@export_group("Speeds")
@export var look_speed : float = 0.002
@export var base_speed : float = 7.0
@export var jump_velocity : float = 4.5
@export var sprint_speed : float = 10.0
@export var freefly_speed : float = 25.0

@export_group("Input Actions")
@export var input_left : String = "ui_left"
@export var input_right : String = "ui_right"
@export var input_forward : String = "ui_up"
@export var input_back : String = "ui_down"
@export var input_jump : String = "ui_accept"
@export var input_sprint : String = "sprint"
@export var input_freefly : String = "freefly"

var mouse_captured : bool = false
var look_rotation : Vector2
var move_speed : float = 0.0
var freeflying : bool = false
var is_sliced : bool = false

var default_collision_size := Vector3(0.5, 1.8, 0.5)
var thin_collision_size := Vector3(0.02, 1.8, 0.5) # Adjust as needed

@onready var head: Node3D = $Head
@onready var collider: CollisionShape3D = $Collider

func _ready() -> void:
	check_input_mappings()
	look_rotation.y = rotation.y
	look_rotation.x = head.rotation.x
	# Set visibility for models
	if mesh_3d:
		mesh_3d.visible = true
	if mesh_2d:
		mesh_2d.visible = false

func _unhandled_input(event: InputEvent) -> void:
	if not is_sliced:
		if Input.is_mouse_button_pressed(MOUSE_BUTTON_LEFT):
			capture_mouse()
		if Input.is_key_pressed(KEY_ESCAPE):
			release_mouse()
	if mouse_captured and event is InputEventMouseMotion and not is_sliced:
		rotate_look(event.relative)

	if can_freefly and Input.is_action_just_pressed(input_freefly):
		if not freeflying:
			enable_freefly()
		else:
			disable_freefly()

	if event is InputEventKey and event.keycode == KEY_G and event.pressed and not event.echo:
		is_sliced = !is_sliced
		if is_sliced:
			camera_side.set_current(true)
			camera_main.set_current(false)
			if mesh_3d:
				mesh_3d.visible = false
			if mesh_2d:
				mesh_2d.visible = true
			if collider.shape is BoxShape3D:
				collider.shape.size = thin_collision_size
			release_mouse()
			mouse_captured = false
		else:
			camera_main.set_current(true)
			camera_side.set_current(false)
			if mesh_3d:
				mesh_3d.visible = true
			if mesh_2d:
				mesh_2d.visible = false
			if collider.shape is BoxShape3D:
				collider.shape.size = default_collision_size

func _physics_process(delta: float) -> void:
	if can_freefly and freeflying:
		var input_dir := Input.get_vector(input_left, input_right, input_forward, input_back)
		var motion := (head.global_basis * Vector3(input_dir.x, 0, input_dir.y)).normalized()
		motion *= freefly_speed * delta
		move_and_collide(motion)
		return

	if has_gravity:
		if not is_on_floor():
			velocity += get_gravity() * delta

	if can_jump:
		if Input.is_action_just_pressed(input_jump) and is_on_floor():
			velocity.y = jump_velocity

	if can_sprint and Input.is_action_pressed(input_sprint):
		move_speed = sprint_speed
	else:
		move_speed = base_speed

	if can_move:
		var input_dir := Input.get_vector(input_left, input_right, input_forward, input_back)
		if is_sliced:
			input_dir.x = 0
		var move_dir := (transform.basis * Vector3(input_dir.x, 0, input_dir.y)).normalized()
		if move_dir:
			velocity.x = move_dir.x * move_speed
			velocity.z = move_dir.z * move_speed
		else:
			velocity.x = move_toward(velocity.x, 0, move_speed)
			velocity.z = move_toward(velocity.z, 0, move_speed)
	else:
		velocity.x = 0
		velocity.y = 0

	move_and_slide()
	if is_sliced:
		var side_offset = Vector3(5, 2, 0)
		camera_side.global_position = global_position + (global_transform.basis.x * 5) + Vector3.UP * 2
		camera_side.look_at(global_position, Vector3.UP)

func rotate_look(rot_input : Vector2):
	if is_sliced:
		return
	look_rotation.x -= rot_input.y * look_speed
	look_rotation.x = clamp(look_rotation.x, deg_to_rad(-85), deg_to_rad(85))
	look_rotation.y -= rot_input.x * look_speed
	transform.basis = Basis()
	rotate_y(look_rotation.y)
	head.transform.basis = Basis()
	head.rotate_x(look_rotation.x)

func enable_freefly():
	collider.disabled = true
	freeflying = true
	velocity = Vector3.ZERO

func disable_freefly():
	collider.disabled = false
	freeflying = false

func capture_mouse():
	Input.set_mouse_mode(Input.MOUSE_MODE_CAPTURED)
	mouse_captured = true

func release_mouse():
	Input.set_mouse_mode(Input.MOUSE_MODE_VISIBLE)
	mouse_captured = false

func check_input_mappings():
	if can_move and not InputMap.has_action(input_left):
		push_error("Movement disabled. No InputAction found for input_left: " + input_left)
		can_move = false
	if can_move and not InputMap.has_action(input_right):
		push_error("Movement disabled. No InputAction found for input_right: " + input_right)
		can_move = false
	if can_move and not InputMap.has_action(input_forward):
		push_error("Movement disabled. No InputAction found for input_forward: " + input_forward)
		can_move = false
	if can_move and not InputMap.has_action(input_back):
		push_error("Movement disabled. No InputAction found for input_back: " + input_back)
		can_move = false
	if can_jump and not InputMap.has_action(input_jump):
		push_error("Jumping disabled. No InputAction found for input_jump: " + input_jump)
		can_jump = false
	if can_sprint and not InputMap.has_action(input_sprint):
		push_error("Sprinting disabled. No InputAction found for input_sprint: " + input_sprint)
		can_sprint = false
	if can_freefly and not InputMap.has_action(input_freefly):
		push_error("Freefly disabled. No InputAction found for input_freefly: " + input_freefly)
		can_freefly = false
