using System.Numerics;
using Box2D.NetStandard.Collision.Shapes;
using Box2D.NetStandard.Dynamics.Bodies;
using Box2D.NetStandard.Dynamics.Fixtures;
using Box2D.NetStandard.Dynamics.Joints.Revolute;
using Box2D.NetStandard.Dynamics.World;

namespace CpuRacer.App.Game;

// Real rigid-body car physics via Box2D: a boxy chassis with two wheels pinned rigidly to it
// (RevoluteJoint — a fixed pivot, no suspension travel) so they can still spin to drive but never
// bounce independently of the body. This is what lets the car go airborne off a crest instead of
// staying glued to the terrain contour.
public sealed class CarPhysics
{
    public const float ChassisHalfWidth = 0.7f;
    public const float ChassisHalfHeight = 0.22f;
    public const float WheelRadius = 0.30f;
    private const float WheelLocalOffsetX = 0.78f; // slightly wider than ChassisHalfWidth so the tires peek out past the body
    private const float WheelLocalOffsetY = -0.18f;
    private const float MaxMotorTorque = 60f;
    private const float FlipAngleThreshold = 2.2f; // radians, ~126 degrees
    private const float FlipSettleAngularSpeed = 1.0f;
    private const float RotateSpeed = 1.5f; // rad/s applied while a rotate key is held
    private const float JumpImpulse = 7.2f;

    private readonly World _world = new(new Vector2(0, -10f));
    private readonly Track _track;
    private readonly Body _ground;
    private readonly Body _chassis;
    private readonly Body _rearWheel;
    private readonly Body _frontWheel;
    private readonly RevoluteJoint _rearJoint;
    private readonly RevoluteJoint _frontJoint;

    private int _groundSegmentsBuilt;

    public CarPhysics(Track track, double startX)
    {
        _track = track;

        _ground = _world.CreateBody(new BodyDef { type = BodyType.Static, position = Vector2.Zero });
        ExtendGround();
        AddBackstop();

        float groundY = (float)track.HeightAt(startX);
        var chassisStart = new Vector2((float)startX, groundY + 1.0f);

        _chassis = _world.CreateBody(new BodyDef
        {
            type = BodyType.Dynamic,
            position = chassisStart,
            linearDamping = 0.15f,
            angularDamping = 0.3f
        });
        var chassisShape = new PolygonShape();
        chassisShape.SetAsBox(ChassisHalfWidth, ChassisHalfHeight);
        _chassis.CreateFixture(new FixtureDef { shape = chassisShape, density = 1f, friction = 0.4f, restitution = 0.05f });

        _rearWheel = CreateWheel(chassisStart + new Vector2(-WheelLocalOffsetX, WheelLocalOffsetY));
        _frontWheel = CreateWheel(chassisStart + new Vector2(WheelLocalOffsetX, WheelLocalOffsetY));

        _rearJoint = CreateWheelJoint(_rearWheel);
        _frontJoint = CreateWheelJoint(_frontWheel);
    }

    public Vector2 ChassisPosition => _chassis.Position;
    public float ChassisAngle => NormalizeAngle(_chassis.GetAngle());
    public Vector2 RearWheelPosition => _rearWheel.Position;
    public Vector2 FrontWheelPosition => _frontWheel.Position;

    public bool IsSettledUpsideDown =>
        Math.Abs(ChassisAngle) >= FlipAngleThreshold &&
        Math.Abs(_chassis.GetAngularVelocity()) < FlipSettleAngularSpeed;

    private Body CreateWheel(Vector2 position)
    {
        var body = _world.CreateBody(new BodyDef { type = BodyType.Dynamic, position = position, angularDamping = 0.4f });
        var shape = new CircleShape();
        shape.Set(Vector2.Zero, WheelRadius);
        body.CreateFixture(new FixtureDef { shape = shape, density = 1.2f, friction = 1.6f, restitution = 0.1f });
        return body;
    }

    private RevoluteJoint CreateWheelJoint(Body wheel)
    {
        var def = new RevoluteJointDef();
        def.Initialize(_chassis, wheel, wheel.Position);
        def.enableMotor = true;
        def.maxMotorTorque = MaxMotorTorque;
        def.motorSpeed = 0;
        return (RevoluteJoint)_world.CreateJoint(def);
    }

    // New ground edges are appended as the live CPU trace reveals more track, never rebuilt.
    // Each edge is given "ghost" vertices from its neighbors (SetOneSided) so Box2D's collision
    // system knows about the adjacent segments — without that, a wheel crossing the seam between
    // two independent edges can catch on the joint and get launched, which is what caused the
    // "car pops upward on its own after landing on a slope" glitch.
    public void ExtendGround()
    {
        IReadOnlyList<double> heights = _track.Heights;
        int totalSegments = Math.Max(0, heights.Count - 1);

        Vector2 PointAt(int index)
        {
            int clamped = Math.Clamp(index, 0, heights.Count - 1);
            return new Vector2((float)(clamped * Track.SampleSpacing), (float)heights[clamped]);
        }

        for (int i = _groundSegmentsBuilt; i < totalSegments; i++)
        {
            var ghostBefore = PointAt(i - 1);
            var p1 = PointAt(i);
            var p2 = PointAt(i + 1);
            var ghostAfter = PointAt(i + 2);

            var edge = new EdgeShape();
            edge.SetOneSided(ghostBefore, p1, p2, ghostAfter);
            _ground.CreateFixture(new FixtureDef { shape = edge, friction = 1.0f });
        }
        _groundSegmentsBuilt = totalSegments;
    }

    // A long flat platform behind X=0, level with the track's first sample. Without this, reversing
    // right at the start of a run drives straight off the edge of the not-yet-generated track and
    // falls out of the world — the live CPU trace only ever grows forward, so there's nothing behind it.
    private void AddBackstop()
    {
        float h0 = (float)_track.HeightAt(0);
        var farLeft = new Vector2(-1000f, h0);
        var start = new Vector2(0f, h0);
        var next = _track.Heights.Count > 1
            ? new Vector2((float)Track.SampleSpacing, (float)_track.Heights[1])
            : start;

        var edge = new EdgeShape();
        edge.SetOneSided(farLeft, farLeft, start, next);
        _ground.CreateFixture(new FixtureDef { shape = edge, friction = 1.0f });
    }

    // throttleLevel is a persistent cruise setting in [-1, 1], not a momentary accelerate/brake press
    // (like a plane's throttle lever: it stays wherever it was last set until nudged again).
    public void SetDrive(double throttleLevel, double difficultyFactor)
    {
        float driveSpeed = 42f * (float)difficultyFactor;
        _rearJoint.SetMotorSpeed(-driveSpeed * (float)throttleLevel);
        _frontJoint.SetMotorSpeed(-driveSpeed * (float)throttleLevel);
    }

    // Manual rotation control (Left/Right): lets the player tip the nose for a cleaner landing after
    // a jump, or rock the car off a peak it's stuck balanced on. direction is -1, 0, or 1.
    public void ApplyRotationInput(int direction)
    {
        if (direction != 0) _chassis.SetAngularVelocity(-direction * RotateSpeed);
    }

    // Space, gated by a cooldown in GameEngine — a straight-up impulse on the chassis; the wheels
    // get pulled along through their joints on the next Step.
    public void Jump() => _chassis.ApplyLinearImpulseToCenter(new Vector2(0, JumpImpulse), true);

    public void Step(float dt) => _world.Step(dt, 8, 3);

    public void Recover()
    {
        float safeX = Math.Max(0, ChassisPosition.X - 1.5f);
        float groundY = (float)_track.HeightAt(safeX);
        var chassisPos = new Vector2(safeX, groundY + 1.0f);

        _chassis.SetTransform(chassisPos, 0f);
        _chassis.SetLinearVelocity(Vector2.Zero);
        _chassis.SetAngularVelocity(0f);

        _rearWheel.SetTransform(chassisPos + new Vector2(-WheelLocalOffsetX, WheelLocalOffsetY), 0f);
        _rearWheel.SetLinearVelocity(Vector2.Zero);
        _rearWheel.SetAngularVelocity(0f);

        _frontWheel.SetTransform(chassisPos + new Vector2(WheelLocalOffsetX, WheelLocalOffsetY), 0f);
        _frontWheel.SetLinearVelocity(Vector2.Zero);
        _frontWheel.SetAngularVelocity(0f);
    }

    private static float NormalizeAngle(float angle) => MathF.Atan2(MathF.Sin(angle), MathF.Cos(angle));
}
