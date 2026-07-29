import { writeSessionFile } from '../session_file.mjs';
import {
  createSessionLeaseToken,
  startSessionLeaseMonitor,
} from '../session_lease.mjs';

const sessionPath = process.argv[2];
if (!sessionPath) throw new Error('Expected a session-file path.');

const publisherLeaseToken = createSessionLeaseToken();
if (!writeSessionFile(sessionPath, { publisherLeaseToken })) {
  throw new Error('Could not create the lease fixture session.');
}

startSessionLeaseMonitor(sessionPath, publisherLeaseToken, {
  intervalMilliseconds: 25,
  onRevoked: (reason) => {
    process.stdout.write(`REVOKED ${reason}\n`);
    process.exit(0);
  },
});
process.stdout.write('READY\n');
