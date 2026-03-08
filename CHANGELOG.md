# Changelog

## [v0.1.2](https://github.com/Exoridus/fahrenheit-parry-mod/releases/tag/v0.1.2) (2026-03-08)
[Full Changelog](https://github.com/Exoridus/fahrenheit-parry-mod/compare/v0.1.1...v0.1.2) | [Previous Releases](https://github.com/Exoridus/fahrenheit-parry-mod/releases)

- refactor(state): read spam controller state directly, remove shadow copies from ParryRuntimeState ([f040e77](https://github.com/Exoridus/fahrenheit-parry-mod/commit/f040e77294c9afedba199eeb64650c5011116a6c))
- fix(config): use File.Move for atomic settings file replacement ([5de85b9](https://github.com/Exoridus/fahrenheit-parry-mod/commit/5de85b9d1455ed6aa910b564f44e969c96940565))
- fix(combat): guard try_get_chr against returning pointers to unoccupied slots ([6818ee0](https://github.com/Exoridus/fahrenheit-parry-mod/commit/6818ee0bf77b3984c07de15520e2a0c59c5b71af))
- fix: preserve feedback overlay state when clearFeedbackFlashes is false ([ff89fe1](https://github.com/Exoridus/fahrenheit-parry-mod/commit/ff89fe17e52369b686ffb1a8fd86d74708bd11bd))
- fix(audio): use SND_PURGE to prevent GCHandle use-after-free ([fb44bc6](https://github.com/Exoridus/fahrenheit-parry-mod/commit/fb44bc692769bb0592f0de8a3493769c7107037f))
