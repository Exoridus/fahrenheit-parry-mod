DOTNET ?= dotnet
CONFIGURATION ?= Debug
FAHRENHEIT_REPO ?= https://github.com/peppy-enterprises/fahrenheit.git
FAHRENHEIT_DIR ?= .workspace/fahrenheit
NATIVE_MSBUILD_EXE ?=
GAME_DIR ?=
MOD_ID ?= fhparry
CI ?= false
DRY_RUN ?= false

LOCAL_CONFIG ?= .workspace/dev.local.mk
-include $(LOCAL_CONFIG)

MSBUILD_BASE_COMMON = $(DOTNET) msbuild build.proj -nologo -verbosity:minimal -p:FahrenheitRepo="$(FAHRENHEIT_REPO)" -p:FahrenheitDir="$(FAHRENHEIT_DIR)"
MSBUILD_BASE = $(MSBUILD_BASE_COMMON) -p:Configuration="$(CONFIGURATION)"
MSBUILD_NATIVE_ARG =
ifneq ($(strip $(NATIVE_MSBUILD_EXE)),)
MSBUILD_NATIVE_ARG = -p:NativeMSBuildExe="$(NATIVE_MSBUILD_EXE)"
endif
MSBUILD_BUILD = $(MSBUILD_BASE) $(MSBUILD_NATIVE_ARG)

.PHONY: help install setup setup-game-dir build build-full build-release deploy deploy-clean deploy-mod build-and-deploy build-full-and-deploy

help:
	@echo Targets:
	@echo   make install               - install/check full prerequisite set via winget
	@echo   make setup                 - setup workspace + optional game path config + optional first full build
	@echo   make setup-game-dir        - detect/prompt and save GAME_DIR in .workspace/dev.local.mk
	@echo   make build                 - default dev build (mod-only, Debug)
	@echo   make build-full            - full Fahrenheit build (Debug by default)
	@echo   make build-release         - full Fahrenheit build (Release)
	@echo   make deploy                - deploy full build to GAME_DIR/fahrenheit (no delete)
	@echo   make deploy-clean          - deploy full build to GAME_DIR/fahrenheit (clean mirror)
	@echo   make deploy-mod            - deploy only fhparry mod folder (no delete)
	@echo   make build-and-deploy      - build mod + deploy-mod
	@echo   make build-full-and-deploy - build-full + deploy
	@echo Variables:
	@echo   DOTNET=dotnet_executable
	@echo   CONFIGURATION=Debug_or_Release
	@echo   FAHRENHEIT_REPO=git_url
	@echo   FAHRENHEIT_DIR=workspace_path
	@echo   NATIVE_MSBUILD_EXE=optional_full_path_to_MSBuild.exe
	@echo   GAME_DIR=path_to_game_root_containing_FFX.exe
	@echo   MOD_ID=mod_id_default_fhparry
	@echo   CI=true_to_disable_interactive_setup_prompts
	@echo   DRY_RUN=true_to_preview_installs_without_changing_system

install:
	cmd /C scripts\install-prerequisites.cmd full $(if $(filter true,$(DRY_RUN)),--dry-run,)

setup:
	$(MSBUILD_BASE) -t:Setup
ifeq ($(CI),true)
	@echo CI=true detected; skipping interactive setup prompts.
else
	cmd /C scripts\setup-interactive.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "$(FAHRENHEIT_REPO)" "$(FAHRENHEIT_DIR)" "$(NATIVE_MSBUILD_EXE)"
endif

setup-game-dir:
	cmd /C scripts\setup-game-dir.cmd "$(GAME_DIR)"

build:
	$(MSBUILD_BASE) -t:BuildModOnly

build-full:
	$(MSBUILD_BUILD) -t:Build

build-release:
	$(MSBUILD_BASE_COMMON) $(MSBUILD_NATIVE_ARG) -t:Build -p:Configuration="Release"

deploy:
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "full" "false" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"

deploy-clean:
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "full" "true" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"

deploy-mod:
	cmd /C scripts\deploy.cmd "$(GAME_DIR)" "$(CONFIGURATION)" "mod" "false" "$(FAHRENHEIT_DIR)" "$(MOD_ID)"

build-and-deploy: build deploy-mod

build-full-and-deploy: build-full deploy

