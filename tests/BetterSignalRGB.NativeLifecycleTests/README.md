# Native lifecycle failure tests

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project tests/BetterSignalRGB.NativeLifecycleTests -c Release
```

This console harness links the production `NativeIntegrationService`. Its API,
control, output, descriptor and preference boundaries are test doubles that can
fail independently. It opens no listener, mapping, profile file or application UI.

The assertions cover initialization arriving after shutdown, lease cleanup despite
an API stop failure, output and descriptor cleanup despite later failures, failed
activation rollback, and normal disable/re-enable behavior. Exceptions remain
visible to callers; failed activation does not persist the enabled preference.
Descriptor cleanup failures also clear the retained ownership before a later
disposal, avoiding a second operation on an already released connection.

This suite verifies lifecycle orchestration under injected faults. Real Kestrel,
descriptor ACL, shared-memory transport and compositor behavior are covered by
`BetterSignalRGB.NativeIntegrationTests`; actual scene/lease restoration is covered
by `BetterSignalRGB.ViewModelTests`.
