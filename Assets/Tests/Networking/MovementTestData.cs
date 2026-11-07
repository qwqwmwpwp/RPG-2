using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    internal static class MovementTestData
    {
        internal static PlayerMovementBaseline Baseline(uint epoch = 1, uint correction = 0, ulong step = 0)
            => new PlayerMovementBaseline { Epoch = epoch, CorrectionId = correction, ConstraintRevision = 1,
                Step = step, ProcessedInputSequence = step,
                State = new PlayerNetworkState { Tick = 100, Time = 2d, Alive = true, CanStand = true } };

        internal static PlayerMovementReport Report(ulong step = 1, uint epoch = 1, uint correction = 0)
            => new PlayerMovementReport { Epoch = epoch, CorrectionId = correction, ConstraintRevision = 1,
                Step = step, Input = new PlayerInputPacket { Sequence = step, ObservedServerTick = 100,
                    Move = Vector2.up }, PredictedMotor = new PlayerMotorState() };

        internal static PlayerMovementRecord Record(ulong step, uint epoch = 1, uint correction = 0)
            => new PlayerMovementRecord { Report = Report(step, epoch, correction),
                PredictedState = new PlayerNetworkState { Tick = 100 + (long)step, Time = 2d + step * .02d } };

        internal static MovementRuleContext Rules()
            => new MovementRuleContext { Active = true, Alive = true, CanUseWeapon = true, CanStand = true,
                ExplorationMoveSpeed = 5f, AimMoveSpeed = 3f, HipFireMoveSpeed = 4f,
                ReloadMoveSpeed = 2.5f, CrouchMoveSpeed = 2f,
                FreeMoveInputThreshold = .1f, StrafeMoveInputThreshold = .05f };

        internal static MovementKinematicsSettings Settings()
            => new MovementKinematicsSettings { Gravity = -9.81f };

        internal static PlayerMovementAck Ack(ulong acceptedStep, uint epoch = 1, uint correction = 0,
            uint constraint = 1)
            => new PlayerMovementAck { Epoch = epoch, CorrectionId = correction, ConstraintRevision = constraint,
                AcceptedStep = acceptedStep, ProcessedInputSequence = acceptedStep, ServerTick = 100 + (long)acceptedStep };
    }
}
