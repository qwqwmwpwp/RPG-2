namespace RPG4.PlayerMovement
{
    internal readonly struct PlayerCameraTarget
    {
        internal readonly bool Attacking;
        internal readonly float Distance, FieldOfView, ExplorationDistance;
        internal PlayerCameraTarget(bool attacking, float distance, float fieldOfView, float explorationDistance)
        {
            Attacking = attacking; Distance = distance; FieldOfView = fieldOfView;
            ExplorationDistance = explorationDistance;
        }
    }
}
