# Contributing to TopiaForge

TopiaForge accepts contributions under the GNU Affero General Public License,
version 3 or later (`AGPL-3.0-or-later`). Inbound terms match outbound terms:
by signing off a contribution you license it under the same terms the project
distributes. Third-party materials keep their original licenses and must be
identified in `THIRD_PARTY_NOTICES.md` with source, license, and modified-file
details.

## Developer Certificate of Origin

Contributions made after the `v1.0.0-rc.1` cutover must certify the
[Developer Certificate of Origin 1.1](DCO). Add a trailer to every commit:

```text
Signed-off-by: Your Name <your-email@example.com>
```

Git can add it with `git commit --signoff`. The trailer states that you have
the right to submit the work under the project's license; it is not a copyright
assignment. Existing history before `v1.0.0-rc.1` is grandfathered.

## Pull requests

- Target `dev` for ordinary changes and use a Conventional Commits title.
- Keep commits focused and include tests for changed behavior.
- Do not commit Robotopia assemblies, release private keys, credentials, or
  generated secrets.
- Preserve third-party notices and follow the repository's clean-room rules.
