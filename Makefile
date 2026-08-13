SOLUTION := KevinZonda.KTerm.slnx
PROJECT := src/KevinZonda.KTerm/KevinZonda.KTerm.csproj
WEB_DIR := src/KevinZonda.KTerm.Web
SMOKE_TEST := scripts/smoke.ps1
PUBLISH_DIR := src/KevinZonda.KTerm/bin/Release/net10.0-windows/win-x64/publish
PUBLISH_EXE := $(PUBLISH_DIR)/KevinZonda.KTerm.exe
INSTALL_DIR := C:/Tools/Bin
INSTALL_EXE := $(INSTALL_DIR)/zt.exe

CONFIG ?= Debug

.DEFAULT_GOAL := build

.PHONY: help deps install restore web build run test format audit publish clean

help:
	@echo "Available targets:"
	@echo "  make deps      - restore NuGet and pnpm dependencies"
	@echo "  make install   - publish and install C:\Tools\Bin\zt.exe"
	@echo "  make web       - type-check and build the web frontend"
	@echo "  make build     - build KTerm; CONFIG=Debug by default"
	@echo "  make run       - build and run KTerm"
	@echo "  make test      - run the 2x2 ConPTY smoke test"
	@echo "  make format    - verify C# formatting"
	@echo "  make audit     - audit NuGet and pnpm dependencies"
	@echo "  make publish   - publish ReadyToRun single-file win-x64"
	@echo "  make clean     - clean .NET build outputs"

deps: restore
	pnpm --dir $(WEB_DIR) install --frozen-lockfile

install: publish
	powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path '$(INSTALL_DIR)' | Out-Null; Copy-Item -Force -LiteralPath '$(PUBLISH_EXE)' -Destination '$(INSTALL_EXE)'"
	@echo "Installed $(INSTALL_EXE)"

restore:
	dotnet restore $(SOLUTION) --nologo

web:
	pnpm --dir $(WEB_DIR) run build

build:
	dotnet build $(SOLUTION) -c $(CONFIG) --nologo

run:
	dotnet run --project $(PROJECT) -c $(CONFIG)

test:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SMOKE_TEST)

format:
	dotnet format $(SOLUTION) --verify-no-changes --no-restore

audit:
	dotnet list $(PROJECT) package --vulnerable --include-transitive
	pnpm --dir $(WEB_DIR) audit --audit-level high

publish:
	dotnet publish $(PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo

clean:
	dotnet clean $(SOLUTION) -c $(CONFIG) --nologo
