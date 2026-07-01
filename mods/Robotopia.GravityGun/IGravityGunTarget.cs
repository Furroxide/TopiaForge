using UnityEngine;

namespace Robotopia.GravityGun
{
    internal interface IGravityGunTarget
    {
        string Name { get; }
        Vector3 Position { get; }
        bool IsAlive { get; }

        void UpdateHold(Camera camera, float holdDistance, GravityGunConfig config, float deltaTime);
        void Throw(Vector3 direction, GravityGunConfig config);
        void Release();
    }
}
