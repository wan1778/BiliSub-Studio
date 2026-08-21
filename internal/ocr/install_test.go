package ocr

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func writeHealthyPaddleInstall(t *testing.T, root string) string {
	t.Helper()
	paths, err := installPaths(root, DeviceCPU, GPUInfo{})
	if err != nil {
		t.Fatal(err)
	}
	python := paths.Python
	if err := os.MkdirAll(filepath.Dir(python), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(python, []byte("fixture"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(root, "models"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := writeWorker(root); err != nil {
		t.Fatal(err)
	}
	if err := writeInstallManifest(root, DeviceCPU, GPUInfo{}); err != nil {
		t.Fatal(err)
	}
	return python
}

func TestManagerRootIsExactGenericOCRDirectory(t *testing.T) {
	root := filepath.Join(t.TempDir(), "Tools", "OCR")
	python := writeHealthyPaddleInstall(t, root)
	m := New(root)
	got, err := m.ensureInstalled(context.Background(), DeviceCPU, GPUInfo{})
	if err != nil {
		t.Fatal(err)
	}
	if got.Python != python {
		t.Fatalf("got %q want %q", got.Python, python)
	}
}

func TestValidatePaddleInstallRejectsWorkerDrift(t *testing.T) {
	root := filepath.Join(t.TempDir(), "OCR")
	writeHealthyPaddleInstall(t, root)
	if err := os.WriteFile(filepath.Join(root, workerFileName), []byte("changed"), 0o644); err != nil {
		t.Fatal(err)
	}
	err := validateInstall(root, DeviceCPU, GPUInfo{})
	if err == nil || !strings.Contains(err.Error(), "worker") {
		t.Fatalf("expected worker-integrity error, got %v", err)
	}
}

func TestInstallManifestPinsRuntimeAndModels(t *testing.T) {
	root := t.TempDir()
	writeHealthyPaddleInstall(t, root)
	b, err := os.ReadFile(runtimeManifestPath(root, DeviceCPU))
	if err != nil {
		t.Fatal(err)
	}
	var got installManifest
	if err := json.Unmarshal(b, &got); err != nil {
		t.Fatal(err)
	}
	if got.PaddleOCR != paddleOCRVersion || got.Paddle != paddleVersion || got.DetModel != detModelName || got.RecModel != recModelName || got.Runtime != DeviceCPU || got.PaddlePkg != "paddlepaddle" {
		t.Fatalf("manifest=%+v", got)
	}
	sum := sha256.Sum256(workerSource)
	if got.WorkerSHA256 != hex.EncodeToString(sum[:]) {
		t.Fatalf("worker hash=%q", got.WorkerSHA256)
	}
}

func TestGPUInstallManifestUsesSeparateRuntimeAndOfficialWheelIndex(t *testing.T) {
	root := t.TempDir()
	gpu := GPUInfo{Detected: true, Usable: true, Name: "fixture", Driver: "550.54", IndexURL: paddleGPU126Index}
	paths, err := installPaths(root, DeviceGPU, gpu)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(paths.Python, filepath.Join("runtime", "gpu", "venv")) || paths.Device != "gpu:0" {
		t.Fatalf("gpu paths=%+v", paths)
	}
	want, err := expectedInstallManifest(DeviceGPU, gpu)
	if err != nil {
		t.Fatal(err)
	}
	if want.PaddlePkg != "paddlepaddle-gpu" || want.PaddleIndex != paddleGPU126Index || want.Runtime != DeviceGPU {
		t.Fatalf("gpu manifest=%+v", want)
	}
}

func TestLegacyRapidOCRDirectoryIsRemovedOnlyAfterNewEngineReady(t *testing.T) {
	base := t.TempDir()
	root := filepath.Join(base, "Tools", "OCR")
	legacy := filepath.Join(base, "Tools", "RapidOCR")
	if err := os.MkdirAll(legacy, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(legacy, "old.bin"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	writeHealthyPaddleInstall(t, root)
	m := New(root)
	if err := m.cleanupLegacy(); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(legacy); !os.IsNotExist(err) {
		t.Fatalf("legacy OCR directory still exists: %v", err)
	}
}

func TestManagedInstallerIsOneClickAndLeavesHealthyPrivateRuntime(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("fixture uses a POSIX shell as fake uv")
	}
	root := filepath.Join(t.TempDir(), "Tools", "OCR")
	bootstrap := filepath.Join(root, "bootstrap")
	if err := os.MkdirAll(bootstrap, 0o755); err != nil {
		t.Fatal(err)
	}
	uv := filepath.Join(bootstrap, "uv.exe")
	script := `#!/bin/sh
if [ "$1" = "venv" ]; then
  target="$2"
  mkdir -p "$target/Scripts"
  printf '%s\n' '#!/bin/sh' 'exit 0' > "$target/Scripts/python.exe"
  chmod +x "$target/Scripts/python.exe"
fi
exit 0
`
	if err := os.WriteFile(uv, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	m := New(root)
	if err := m.installManagedRuntime(context.Background(), DeviceCPU, GPUInfo{}); err != nil {
		t.Fatal(err)
	}
	if err := validateInstall(root, DeviceCPU, GPUInfo{}); err != nil {
		t.Fatalf("private runtime not healthy after one installer call: %v", err)
	}
	paths, _ := installPaths(root, DeviceCPU, GPUInfo{})
	if _, err := os.Stat(paths.Python); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(root, "worker.py")); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(root, "models")); err != nil {
		t.Fatal(err)
	}
}

func TestManagedRuntimeEnvironmentStaysPortableOnWindows(t *testing.T) {
	root := filepath.Join(t.TempDir(), "Tools", "OCR")
	env := managedRuntimeEnv(root)
	joined := "\n" + strings.Join(env, "\n") + "\n"
	for _, want := range []string{
		"UV_PYTHON_INSTALL_DIR=" + filepath.Join(root, "python"),
		"UV_PYTHON_BIN_DIR=" + filepath.Join(root, "python-bin"),
		"UV_PYTHON_INSTALL_REGISTRY=0",
		"UV_MANAGED_PYTHON=1",
	} {
		if !strings.Contains(joined, "\n"+want+"\n") {
			t.Fatalf("managed OCR env missing %q", want)
		}
	}
}
