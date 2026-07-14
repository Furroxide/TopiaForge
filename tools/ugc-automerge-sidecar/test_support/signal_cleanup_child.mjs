import { writeSessionFile } from '../session_file.mjs';
import { createSessionLeaseToken } from '../session_lease.mjs';

const sessionPath = process.argv[2];
if (!sessionPath) throw new Error('Expected a session-file path.');

const publisherLeaseToken = createSessionLeaseToken();
if (!writeSessionFile(sessionPath, { publisherLeaseToken })) {
  throw new Error('Could not create the signal-cleanup fixture session.');
}

const keepAlive = setInterval(() => {}, 1000);
process.once('SIGTERM', () => {
  clearInterval(keepAlive);
  // A compare-then-delete of the shared path could erase a replacement lease.
  // PID liveness validation and explicit cleanup handle this stale file.
  process.stdout.write('SIGNALLED lease-preserved\n');
  process.exit(0);
});
process.stdout.write('READY\n');
