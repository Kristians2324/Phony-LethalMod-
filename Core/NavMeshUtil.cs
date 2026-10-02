using UnityEngine;
using UnityEngine.AI;

namespace Phoney.Core;

/// <summary>
/// Safe utility methods for NavMeshAgent operations to prevent Unity errors
/// such as: "Resume can only be called on an active agent that has been placed on a NavMesh."
/// </summary>
public static class NavMeshUtil
{
    public static void SafeSetStopped(NavMeshAgent? agent, bool stopped)
    {
        if (agent != null && agent.isOnNavMesh)
        {
            if (agent.isStopped != stopped)
            {
                agent.isStopped = stopped;
            }
        }
    }

    public static void SafeSetVelocity(NavMeshAgent? agent, Vector3 velocity)
    {
        if (agent != null && agent.isOnNavMesh)
        {
            agent.velocity = velocity;
        }
    }

    public static void SafeSetSpeed(NavMeshAgent? agent, float speed)
    {
        if (agent != null && agent.isOnNavMesh)
        {
            agent.speed = speed;
        }
    }

    public static bool SafeSetDestination(NavMeshAgent? agent, Vector3 destination)
    {
        if (agent != null && agent.isOnNavMesh)
        {
            return agent.SetDestination(destination);
        }
        return false;
    }

    public static bool SafeWarp(NavMeshAgent? agent, Vector3 position)
    {
        if (agent == null) return false;
        try
        {
            if (NavMesh.SamplePosition(position, out var hit, 2.5f, NavMesh.AllAreas))
            {
                position = hit.position;
            }

            if (!agent.enabled)
            {
                agent.transform.position = position;
                agent.enabled = true;
            }

            return agent.Warp(position);
        }
        catch
        {
            if (agent != null) agent.transform.position = position;
            return false;
        }
    }
}
