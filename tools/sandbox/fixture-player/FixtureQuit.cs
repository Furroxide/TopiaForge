using System;
using System.Globalization;
using UnityEngine;

// Minimal verification fixture for the provisioning launch/log/wait mechanism. It mirrors the loader's
// deferred quit: one Application.Quit request from the first Update, then phase lines with UTC times.
// It contains no game code and proves nothing about Robotopia, isolation or acceptance.
public sealed class FixtureQuit : MonoBehaviour
{
    private bool requested;
    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private void Awake() { Debug.Log("FixtureQuit: Awake " + Now()); }
    private void Update()
    {
        if (requested) return;
        requested = true;
        Debug.Log("FixtureQuit: first Update; requesting Unity quit once " + Now());
        Application.Quit(0);
        Debug.Log("FixtureQuit: quit call returned " + Now());
    }
    private void OnApplicationQuit() { Debug.Log("FixtureQuit: OnApplicationQuit " + Now()); }
    private void OnDestroy() { Debug.Log("FixtureQuit: OnDestroy " + Now()); }
}
