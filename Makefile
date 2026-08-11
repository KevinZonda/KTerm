SOLUTION := KevinZonda.KTerm.slnx
PROJECT := src/KevinZonda.KTerm/KevinZonda.KTerm.csproj
WEB_DIR := src/KevinZonda.KTerm.Web
SMOKE_TEST := scripts/smoke.ps1

CONFIG ?= Debug
RID ?= win-x64

.DEFAULT_GOAL := build

.PHONY: help install restore web build run test format audit publish clean

help:
	@echo "Available targets:"
	@echo "  make install   - restore NuGet and npm dependencies"
	@echo "  make web       - type-check and build the web frontend"
	@echo "  make build     - build KTerm; CONFIG=Debug by default"
	@echo "  make run       - build and run KTerm"
	@echo "  make test      - run the 2x2 ConPTY smoke test"
	@echo "  make format    - verify C# formatting"
	@echo "  make audit     - audit NuGet and npm dependencies"
	@echo "  make publish   - publish framework-dependent RID=win-x64"
	@echo "  make clean     - clean .NET build outputs"

install: restore
	npm ci --prefix $(WEB_DIR)

restore:
	dotnet restore $(SOLUTION) --nologo

web:
	npm run build --prefix $(WEB_DIR)

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
	npm audit --prefix $(WEB_DIR) --audit-level=high

publish:
	dotnet publish $(PROJECT) -c Release -r $(RID) --self-contained false --nologo

clean:
	dotnet clean $(SOLUTION) -c $(CONFIG) --nologo
