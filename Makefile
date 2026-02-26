DOTNET ?= dotnet
CONFIGURATION ?= Debug
FAHRENHEIT_REPO ?= https://github.com/peppy-enterprises/fahrenheit.git
FAHRENHEIT_DIR ?= .workspace/fahrenheit
NATIVE_MSBUILD_EXE ?=
GAME_DIR ?=
MOD_ID ?= fhparry
DRY_RUN ?= false
REPOSITORY ?=
BUILD_TARGET ?= mod
DEPLOY_TARGET ?= mod
DEPLOY_MODE ?= merge
BUMP ?= patch
COMMIT_TYPE ?= chore
COMMIT_SCOPE ?=
COMMIT_MSG ?=
COMMIT_BREAKING ?= false

LOCAL_CONFIG ?= .workspace/dev.local.mk
-include $(LOCAL_CONFIG)

MSBUILD_BASE_COMMON = $(DOTNET) msbuild build.proj -nologo -verbosity:minimal -p:FahrenheitRepo="$(FAHRENHEIT_REPO)" -p:FahrenheitDir="$(FAHRENHEIT_DIR)"
MSBUILD_BASE = $(MSBUILD_BASE_COMMON) -p:Configuration="$(CONFIGURATION)"
MSBUILD_NATIVE_ARG =
ifneq ($(strip $(NATIVE_MSBUILD_EXE)),)
MSBUILD_NATIVE_ARG = -p:NativeMSBuildExe="$(NATIVE_MSBUILD_EXE)"
endif
MSBUILD_BUILD = $(MSBUILD_BASE) $(MSBUILD_NATIVE_ARG)

.PHONY: help install setup setup-hooks setup-game-dir verify build deploy release-version \
	build-mod build-full build-release deploy-mod deploy-full \
	changelog bump-version commit build-and-deploy build-and-deploy-mod build-and-deploy-full

help:
	@echo Common targets:
	@echo   make setup
	@echo   make commit COMMIT_MSG="message" [COMMIT_TYPE=chore] [COMMIT_SCOPE=optional] [COMMIT_BREAKING=true^|false]
	@echo   make verify [CONFIGURATION=Debug^|Release]
	@echo   make build [BUILD_TARGET=mod^|full] [CONFIGURATION=Debug^|Release]
	@echo   make deploy [DEPLOY_TARGET=mod^|full] [DEPLOY_MODE=merge^|replace] [GAME_DIR=...]
	@echo   make release-version [BUMP=patch^|minor^|major]
	@echo.
	@echo Setup helpers:
	@echo   make install
	@echo   make setup-hooks
	@echo   make setup-game-dir [GAME_DIR=...]
	@echo.
	@echo Minimal overrides:
	@echo   CONFIGURATION=Debug^|Release
	@echo   GAME_DIR=path_to_game_root_containing_FFX.exe
	@echo.
	@echo Examples:
	@echo   make build BUILD_TARGET=mod
	@echo   make build BUILD_TARGET=full CONFIGURATION=Release
	@echo   make deploy DEPLOY_TARGET=full DEPLOY_MODE=replace GAME_DIR="C:\Games\Final Fantasy X-X2 - HD Remaster"
	@echo   make release-version BUMP=minor

install:
	cmd /C scripts\install-prerequisites.cmd full $(if $(filter true,$(DRY_RUN)),--dry-run,)

setup-hooks:
	cmd /C scripts\install-git-hooks.cmd

setup: setup-hooks
	$(MSBUILD_BASE) -t:Setup
ifeq ($(GITHUB_ACTIONS),true)
	@echo GITHUB_ACTIONS=true detected; skipping interactive setup prompts.
else
	cmd /C scripts\setup-interactive.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "$(FAHRENHEIT_REPO)" "$(FAHRENHEIT_DIR)" "$(NATIVE_MSBUILD_EXE)"
endif

setup-game-dir:
	cmd /C scripts\setup-game-dir.cmd "$(GAME_DIR)"

verify:
ifneq ($(strip $(REPOSITORY)),)
	cmd /C scripts\selftest.cmd --repo "$(REPOSITORY)"
else
	cmd /C scripts\selftest.cmd
endif
	$(MAKE) build BUILD_TARGET=mod CONFIGURATION=$(CONFIGURATION)
	cmd /C scripts\run-tests-if-any.cmd --configuration "$(CONFIGURATION)"

build:
ifeq ($(BUILD_TARGET),mod)
	$(MSBUILD_BASE) -t:BuildModOnly
else ifeq ($(BUILD_TARGET),full)
	$(MSBUILD_BUILD) -t:Build
else
	@echo ERROR: invalid BUILD_TARGET="$(BUILD_TARGET)". Use mod or full.
	@exit 2
endif

deploy:
ifeq ($(DEPLOY_TARGET),mod)
ifeq ($(DEPLOY_MODE),merge)
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "mod" "false" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"
else ifeq ($(DEPLOY_MODE),replace)
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "mod" "true" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"
else
	@echo ERROR: invalid DEPLOY_MODE="$(DEPLOY_MODE)". Use merge or replace.
	@exit 2
endif
else ifeq ($(DEPLOY_TARGET),full)
ifeq ($(DEPLOY_MODE),merge)
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "full" "false" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"
else ifeq ($(DEPLOY_MODE),replace)
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "full" "true" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"
else
	@echo ERROR: invalid DEPLOY_MODE="$(DEPLOY_MODE)". Use merge or replace.
	@exit 2
endif
else
	@echo ERROR: invalid DEPLOY_TARGET="$(DEPLOY_TARGET)". Use mod or full.
	@exit 2
endif

release-version:
ifneq ($(strip $(REPOSITORY)),)
	cmd /C scripts\bump-version.cmd --bump "$(BUMP)" --repo "$(REPOSITORY)"
else
	cmd /C scripts\bump-version.cmd --bump "$(BUMP)"
endif

changelog:
ifneq ($(strip $(REPOSITORY)),)
	cmd /C scripts\generate-changelog.cmd --repo "$(REPOSITORY)" --out "CHANGELOG.md"
else
	cmd /C scripts\generate-changelog.cmd --out "CHANGELOG.md"
endif

bump-version: release-version

commit:
ifeq ($(strip $(COMMIT_SCOPE)),)
ifeq ($(filter true,$(COMMIT_BREAKING)),true)
	cmd /C scripts\commit.cmd --type "$(COMMIT_TYPE)" --message "$(COMMIT_MSG)" --breaking
else
	cmd /C scripts\commit.cmd --type "$(COMMIT_TYPE)" --message "$(COMMIT_MSG)"
endif
else
ifeq ($(filter true,$(COMMIT_BREAKING)),true)
	cmd /C scripts\commit.cmd --type "$(COMMIT_TYPE)" --scope "$(COMMIT_SCOPE)" --message "$(COMMIT_MSG)" --breaking
else
	cmd /C scripts\commit.cmd --type "$(COMMIT_TYPE)" --scope "$(COMMIT_SCOPE)" --message "$(COMMIT_MSG)"
endif
endif

# Compatibility aliases (kept intentionally, omitted from help output)
build-mod:
	$(MAKE) build BUILD_TARGET=mod

build-full:
	$(MAKE) build BUILD_TARGET=full

build-release:
	$(MAKE) build BUILD_TARGET=full CONFIGURATION=Release

deploy-mod:
	$(MAKE) deploy DEPLOY_TARGET=mod DEPLOY_MODE=merge

deploy-full:
	$(MAKE) deploy DEPLOY_TARGET=full DEPLOY_MODE=merge

build-and-deploy-mod:
	$(MAKE) build BUILD_TARGET=mod
	$(MAKE) deploy DEPLOY_TARGET=mod DEPLOY_MODE=merge

build-and-deploy: build-and-deploy-mod

build-and-deploy-full:
	$(MAKE) build BUILD_TARGET=full
	$(MAKE) deploy DEPLOY_TARGET=full DEPLOY_MODE=merge

