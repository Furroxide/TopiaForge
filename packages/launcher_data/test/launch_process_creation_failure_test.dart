import 'dart:io';
import 'package:launcher_data/src/launch_process_control.dart';
import 'package:test/test.dart';
import 'launch_process_creation_failure_fixture.dart';

void main() {
  group(
    'Original Windows creation handles on native failure',
    () {
      for (final fault in [
        NativeCreationFault.times,
        NativeCreationFault.resume,
      ]) {
        test(
          '${fault.name} failure drains only its original suspended child',
          () async {
            final fixture = await NativeFailureFixture.create(fault);
            addTearDown(fixture.dispose);
            await fixture.startSibling();
            LaunchProcessReceipt? receipt;
            await expectLater(
              fixture.start().then((value) => receipt = value),
              throwsA(
                isA<StateError>().having(
                  (error) => error.message,
                  'message',
                  contains(
                    fault == NativeCreationFault.times
                        ? 'identity could not be read'
                        : 'could not be resumed',
                  ),
                ),
              ),
            );
            expect(receipt, isNull);
            expect(await fixture.marker.exists(), isFalse);
            final calls = fixture.calls;
            final process = calls.processCalls;
            expect(calls.creations, 1);
            expect(calls.inheritHandles, 0);
            expect(calls.creationFlags! & 4, 4);
            expect(
              calls.monitoredExited,
              isTrue,
              reason:
                  'The duplicate proves exit of the original process object.',
            );
            expect(process.timesCalls, 1);
            expect(
              process.imageCalls,
              fault == NativeCreationFault.times ? 0 : 1,
            );
            expect(calls.resumes, fault == NativeCreationFault.times ? 0 : 1);
            expect(process.terminations, 1);
            expect(
              process.opens,
              0,
              reason: 'Creation cleanup must never reopen a PID.',
            );
            expect(process.closedHandles, [
              calls.thread.address,
              calls.process.address,
            ]);
            expect(process.allocations, greaterThan(0));
            expect(process.frees, process.allocations);
            expect(process.activeAllocations, isEmpty);
            expect(process.errors, isEmpty);
            final terminated = process.events.indexOf('terminate');
            final exited = process.events.lastIndexOf('wait:0');
            final closed = process.events.indexWhere(
              (value) => value.startsWith('close:'),
            );
            expect(terminated, greaterThanOrEqualTo(0));
            expect(exited, greaterThan(terminated));
            expect(closed, greaterThan(exited));
            await fixture.proveSiblingResponsive();
            expect(
              fixture.siblingExited,
              isFalse,
              reason: 'The independently owned same-image child must survive.',
            );
            expect(
              int.parse(await fixture.siblingMarker.readAsString()),
              fixture.sibling!.pid,
            );
            expect(calls.processId, isNot(fixture.sibling!.pid));
          },
        );
      }

      test('child PID marker is hidden until publication is complete', () async {
        final fixture = await NativeFailureFixture.create(
          NativeCreationFault.none,
        );
        addTearDown(fixture.dispose);
        final receipt = await fixture.start(pausePublication: true);
        try {
          await waitForFile(fixture.publicationReady);
          expect(
            await fixture.marker.exists(),
            isFalse,
            reason:
                'Opening an empty publication file cannot advertise a complete child PID.',
          );
        } finally {
          // Release the real child even when the regression assertion fails.
          await fixture.publicationResume.writeAsString('continue');
        }
        await waitForFile(fixture.marker);
        expect(int.parse(await fixture.marker.readAsString()), receipt.pid);
        await fixture.stop.writeAsString('stop');
      });

      test(
        'real delegated calls return a live receipt and release every original resource',
        () async {
          final fixture = await NativeFailureFixture.create(
            NativeCreationFault.none,
          );
          addTearDown(fixture.dispose);
          await fixture.startSibling();
          final receipt = await fixture.start();
          await waitForFile(fixture.marker);
          final calls = fixture.calls;
          final process = calls.processCalls;
          expect(receipt.pid, calls.processId);
          expect(receipt.identity, isNotNull);
          expect(
            receipt.identity!.pid,
            int.parse(await fixture.marker.readAsString()),
          );
          expect(receipt.identity!.nativeStartToken, startsWith('windows:'));
          expect(calls.monitoredExited, isFalse);
          expect(process.timesCalls, 1);
          expect(process.imageCalls, 1);
          expect(calls.resumes, 1);
          expect(process.terminations, 0);
          expect(process.opens, 0);
          expect(process.closedHandles, [
            calls.thread.address,
            calls.process.address,
          ]);
          expect(process.frees, process.allocations);
          expect(process.activeAllocations, isEmpty);
          expect(process.errors, isEmpty);
          await fixture.proveSiblingResponsive();
          expect(fixture.siblingExited, isFalse);
          await fixture.stop.writeAsString('stop');
        },
      );
    },
    skip: !Platform.isWindows,
  );
}
