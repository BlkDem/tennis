using System;
using UnityEngine;

namespace TennisSim.Simulation
{
    [Serializable]
    public class PlayerMovementTuning
    {
        [Header("Speed, m/s")]
        public float RunSpeed = 6.2f;
        public float SprintSpeed = 7.8f;
        [Tooltip("Speed multiplier while preparing a shot under manual control.")]
        public float PreparingSpeedScale = 0.85f;
        public float RecoverySpeedScale = 0.85f;

        [Header("Acceleration, m/s²")]
        public float Acceleration = 30f;
        public float Deceleration = 40f;
        [Tooltip("Used when the stick points against the current movement, so changes of direction feel instant.")]
        public float TurnAcceleration = 55f;

        [Header("Court bounds")]
        public float MaxX = 9f;
        public float MaxBehindBaseline = 6f;
        public float MinDistanceFromNet = 0.6f;

        public Vector3 ClampToHalf(Vector3 position, PlayerSlot slot)
        {
            float forward = CourtGeometry.ForwardSign(slot);
            float distanceFromNet = -position.z * forward;
            distanceFromNet = Mathf.Clamp(distanceFromNet, MinDistanceFromNet, CourtGeometry.HalfLength + MaxBehindBaseline);
            return new Vector3(Mathf.Clamp(position.x, -MaxX, MaxX), position.y, -distanceFromNet * forward);
        }
    }

    /// <summary>Responsive arcade locomotion: instant direction intent limited only by acceleration.</summary>
    public sealed class PlayerMovement
    {
        readonly PlayerSlot slot;
        readonly PlayerMovementTuning tuning;

        public Vector3 Position { get; private set; }
        public Vector3 Velocity { get; private set; }
        public PlayerMovementTuning Tuning => tuning;

        public PlayerMovement(PlayerSlot slot, PlayerMovementTuning tuning)
        {
            this.slot = slot;
            this.tuning = tuning;
        }

        public void Teleport(Vector3 position)
        {
            Position = tuning.ClampToHalf(position, slot);
            Velocity = Vector3.zero;
        }

        public void Tick(Vector3 desiredVelocity, float dt)
        {
            float rate = RateFor(desiredVelocity);
            Velocity = Vector3.MoveTowards(Velocity, desiredVelocity, rate * dt);

            Vector3 next = Position + Velocity * dt;
            Vector3 clamped = tuning.ClampToHalf(next, slot);
            Vector3 velocity = Velocity;
            if (!Mathf.Approximately(clamped.x, next.x)) velocity.x = 0f;
            if (!Mathf.Approximately(clamped.z, next.z)) velocity.z = 0f;

            Velocity = velocity;
            Position = clamped;
        }

        /// <summary>Velocity that reaches a point and stops there without overshooting.</summary>
        public Vector3 ArriveVelocity(Vector3 point, float maxSpeed)
        {
            Vector3 toPoint = new Vector3(point.x - Position.x, 0f, point.z - Position.z);
            float distance = toPoint.magnitude;
            if (distance < 0.02f) return Vector3.zero;
            float speed = Mathf.Min(maxSpeed, Mathf.Sqrt(2f * tuning.Deceleration * distance));
            return toPoint * (speed / distance);
        }

        float RateFor(Vector3 desired)
        {
            float currentSpeed = Velocity.magnitude;
            if (currentSpeed < 0.01f) return tuning.Acceleration;
            if (desired.sqrMagnitude < 0.0001f) return tuning.Deceleration;
            if (Vector3.Dot(Velocity, desired) < 0f) return tuning.TurnAcceleration;
            return desired.magnitude >= currentSpeed ? tuning.Acceleration : tuning.Deceleration;
        }
    }
}
