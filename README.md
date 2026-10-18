# Fandom Score — TODO Pack

This folder contains a starter set of feature-specific `TODO.md` checklists derived from the supplied Fandom Score README. It is intended to be copied into or merged with the project repository.

## How to use
1. Review the actual repository before moving or creating files.
2. Merge these checklists into matching feature folders; do not overwrite existing implementation.
3. Update each checklist as work is verified.
4. Treat unresolved product decisions and proposed values as open until explicitly approved.
5. Keep `PROJECT_TODO.md` as the high-level progress tracker.

## Checklist conventions
- `- [ ]` means not done.
- `- [x]` means completed and verified.
- `README.md` explains scope and interfaces.
- `TODO.md` lists concrete implementation and test tasks.
- Do not mark tests passed unless they were run and passed.

## Important notes
The source specification proposes React Native/Expo, a Node.js or Python API, Redis, an S3-compatible store, and BullMQ or Celery. Do not introduce a second stack if the existing project has already selected one. Verification thresholds, baseline blending parameters, and some integration choices are provisional. Verify external provider requirements before implementation.
