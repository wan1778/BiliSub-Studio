package ocr

import (
	"archive/zip"
	"bilisubstudio/internal/proc"
	"context"
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

const (
	uvVersion           = "0.12.0"
	pythonVersion       = "3.12"
	paddleVersion       = "3.2.0"
	paddleOCRVersion    = "3.7.0"
	detModelName        = "PP-OCRv6_small_det"
	recModelName        = "PP-OCRv6_small_rec"
	workerFileName      = "worker.py"
	installManifestName = "install.json"
	installSchema       = 2
	uvArchiveName       = "uv-x86_64-pc-windows-msvc.zip"
	paddleCPUIndex      = "https://www.paddlepaddle.org.cn/packages/stable/cpu/"
	paddleGPU118Index   = "https://www.paddlepaddle.org.cn/packages/stable/cu118/"
	paddleGPU126Index   = "https://www.paddlepaddle.org.cn/packages/stable/cu126/"
)

var (
	uvArchiveURL  = "https://github.com/astral-sh/uv/releases/download/" + uvVersion + "/" + uvArchiveName
	uvChecksumURL = uvArchiveURL + ".sha256"
)

//go:embed worker.py
var workerSource []byte

type engineInstall struct {
	Python     string
	Worker     string
	ModelCache string
	Device     string
	Runtime    string
}

type installManifest struct {
	Schema       int    `json:"schema"`
	UV           string `json:"uv"`
	Python       string `json:"python"`
	Paddle       string `json:"paddle"`
	PaddleOCR    string `json:"paddleocr"`
	DetModel     string `json:"det_model"`
	RecModel     string `json:"rec_model"`
	WorkerSHA256 string `json:"worker_sha256"`
	Runtime      string `json:"runtime"`
	PaddlePkg    string `json:"paddle_package"`
	PaddleIndex  string `json:"paddle_index"`
}

func runtimeSpec(kind string, gpu GPUInfo) (packageName, indexURL, device string, err error) {
	switch kind {
	case DeviceCPU:
		return "paddlepaddle", paddleCPUIndex, "cpu", nil
	case DeviceGPU:
		if !gpu.Usable || strings.TrimSpace(gpu.IndexURL) == "" {
			msg := strings.TrimSpace(gpu.Error)
			if msg == "" {
				msg = "không có NVIDIA GPU tương thích"
			}
			return "", "", "", errors.New(msg)
		}
		return "paddlepaddle-gpu", gpu.IndexURL, "gpu:0", nil
	default:
		return "", "", "", fmt.Errorf("runtime OCR không hợp lệ: %s", kind)
	}
}

func expectedInstallManifest(kind string, gpu GPUInfo) (installManifest, error) {
	pkg, indexURL, _, err := runtimeSpec(kind, gpu)
	if err != nil {
		return installManifest{}, err
	}
	sum := sha256.Sum256(workerSource)
	return installManifest{
		Schema: installSchema, UV: uvVersion, Python: pythonVersion,
		Paddle: paddleVersion, PaddleOCR: paddleOCRVersion,
		DetModel: detModelName, RecModel: recModelName,
		WorkerSHA256: hex.EncodeToString(sum[:]),
		Runtime:      kind, PaddlePkg: pkg, PaddleIndex: indexURL,
	}, nil
}

func installPaths(root, kind string, gpu GPUInfo) (engineInstall, error) {
	_, _, device, err := runtimeSpec(kind, gpu)
	if err != nil {
		return engineInstall{}, err
	}
	runtimeRoot := filepath.Join(root, "runtime", kind)
	return engineInstall{
		Python:     filepath.Join(runtimeRoot, "venv", "Scripts", "python.exe"),
		Worker:     filepath.Join(root, workerFileName),
		ModelCache: filepath.Join(root, "models"),
		Device:     device,
		Runtime:    kind,
	}, nil
}

func (m *Manager) ensureInstalled(ctx context.Context, kind string, gpu GPUInfo) (engineInstall, error) {
	paths, err := installPaths(m.Root, kind, gpu)
	if err != nil {
		return engineInstall{}, err
	}
	if err := validateInstall(m.Root, kind, gpu); err == nil {
		return paths, nil
	}
	if err := m.installManagedRuntime(ctx, kind, gpu); err != nil {
		return engineInstall{}, err
	}
	if err := validateInstall(m.Root, kind, gpu); err != nil {
		return engineInstall{}, fmt.Errorf("cài bộ nhận diện không đầy đủ: %w", err)
	}
	return paths, nil
}

func (m *Manager) installManagedRuntime(ctx context.Context, kind string, gpu GPUInfo) error {
	if err := os.MkdirAll(m.Root, 0o755); err != nil {
		return err
	}
	uv, err := m.ensureUV(ctx)
	if err != nil {
		return err
	}
	paths, err := installPaths(m.Root, kind, gpu)
	if err != nil {
		return err
	}
	pkg, indexURL, _, err := runtimeSpec(kind, gpu)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(paths.ModelCache, 0o755); err != nil {
		return err
	}
	runtimeRoot := filepath.Join(m.Root, "runtime", kind)
	if err := os.MkdirAll(runtimeRoot, 0o755); err != nil {
		return err
	}
	env := managedRuntimeEnv(m.Root)
	steps := [][]string{
		{"python", "install", pythonVersion, "--install-dir", filepath.Join(m.Root, "python"), "--managed-python", "--no-config"},
		{"venv", filepath.Join(runtimeRoot, "venv"), "--python", pythonVersion, "--managed-python", "--no-config"},
		{"pip", "install", "--python", paths.Python, pkg + "==" + paddleVersion, "--index-url", indexURL, "--no-config"},
		{"pip", "install", "--python", paths.Python, "paddleocr==" + paddleOCRVersion, "--no-config"},
	}
	for _, args := range steps {
		if err := runInstallerCommand(ctx, uv, env, args...); err != nil {
			return err
		}
	}
	if err := writeWorker(m.Root); err != nil {
		return err
	}
	if err := writeInstallManifest(m.Root, kind, gpu); err != nil {
		return err
	}
	return nil
}

func managedRuntimeEnv(root string) []string {
	return append(os.Environ(),
		"UV_PYTHON_INSTALL_DIR="+filepath.Join(root, "python"),
		"UV_PYTHON_BIN_DIR="+filepath.Join(root, "python-bin"),
		"UV_PYTHON_INSTALL_REGISTRY=0",
		"UV_CACHE_DIR="+filepath.Join(root, "cache", "uv"),
		"UV_MANAGED_PYTHON=1",
		"UV_NO_PROGRESS=1",
		"PYTHONUTF8=1",
		"PYTHONIOENCODING=utf-8",
	)
}

func runInstallerCommand(ctx context.Context, exe string, env []string, args ...string) error {
	cmd := proc.Hide(exec.CommandContext(ctx, exe, args...))
	cmd.Env = env
	b, err := cmd.CombinedOutput()
	if err != nil {
		msg := strings.TrimSpace(string(b))
		if msg == "" {
			return fmt.Errorf("cài OCR (%s): %w", strings.Join(args, " "), err)
		}
		return fmt.Errorf("cài OCR (%s): %w: %s", strings.Join(args, " "), err, msg)
	}
	return nil
}

func (m *Manager) ensureUV(ctx context.Context) (string, error) {
	bootstrap := filepath.Join(m.Root, "bootstrap")
	if err := os.MkdirAll(bootstrap, 0o755); err != nil {
		return "", err
	}
	uv := filepath.Join(bootstrap, "uv.exe")
	if st, err := os.Stat(uv); err == nil && !st.IsDir() && st.Size() > 0 {
		return uv, nil
	}
	archive := filepath.Join(bootstrap, uvArchiveName)
	checksum := archive + ".sha256"
	if err := downloadFile(ctx, uvArchiveURL, archive); err != nil {
		return "", fmt.Errorf("tải trình cài OCR: %w", err)
	}
	defer os.Remove(archive)
	if err := downloadFile(ctx, uvChecksumURL, checksum); err != nil {
		return "", fmt.Errorf("tải checksum trình cài OCR: %w", err)
	}
	defer os.Remove(checksum)
	if err := verifySHA256File(archive, checksum); err != nil {
		return "", fmt.Errorf("xác minh trình cài OCR: %w", err)
	}
	if err := unzipNamedFile(archive, "uv.exe", uv); err != nil {
		return "", fmt.Errorf("giải nén trình cài OCR: %w", err)
	}
	return uv, nil
}

func downloadFile(ctx context.Context, url, path string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "BiliSubStudio/4 OCR")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	tmp := path + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	_, copyErr := io.Copy(f, resp.Body)
	syncErr := f.Sync()
	closeErr := f.Close()
	if copyErr != nil || syncErr != nil || closeErr != nil {
		_ = os.Remove(tmp)
		if copyErr != nil {
			return copyErr
		}
		if syncErr != nil {
			return syncErr
		}
		return closeErr
	}
	return os.Rename(tmp, path)
}

func verifySHA256File(path, checksumPath string) error {
	b, err := os.ReadFile(checksumPath)
	if err != nil {
		return err
	}
	fields := strings.Fields(string(b))
	if len(fields) == 0 || len(fields[0]) != 64 {
		return errors.New("checksum SHA-256 không hợp lệ")
	}
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return err
	}
	got := hex.EncodeToString(h.Sum(nil))
	if !strings.EqualFold(got, fields[0]) {
		return fmt.Errorf("SHA-256 sai: %s", got)
	}
	return nil
}

func unzipNamedFile(archive, wanted, dest string) error {
	r, err := zip.OpenReader(archive)
	if err != nil {
		return err
	}
	defer r.Close()
	for _, f := range r.File {
		if !strings.EqualFold(filepath.Base(f.Name), wanted) {
			continue
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		tmp := dest + ".tmp"
		out, err := os.Create(tmp)
		if err != nil {
			rc.Close()
			return err
		}
		_, copyErr := io.Copy(out, rc)
		rc.Close()
		syncErr := out.Sync()
		closeErr := out.Close()
		if copyErr != nil || syncErr != nil || closeErr != nil {
			_ = os.Remove(tmp)
			if copyErr != nil {
				return copyErr
			}
			if syncErr != nil {
				return syncErr
			}
			return closeErr
		}
		return os.Rename(tmp, dest)
	}
	return fmt.Errorf("archive thiếu %s", wanted)
}

func writeWorker(root string) error {
	path := filepath.Join(root, workerFileName)
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, workerSource, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func runtimeManifestPath(root, kind string) string {
	return filepath.Join(root, "runtime", kind, installManifestName)
}

func writeInstallManifest(root, kind string, gpu GPUInfo) error {
	want, err := expectedInstallManifest(kind, gpu)
	if err != nil {
		return err
	}
	b, err := json.MarshalIndent(want, "", "  ")
	if err != nil {
		return err
	}
	path := runtimeManifestPath(root, kind)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, append(b, '\n'), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func validateInstall(root, kind string, gpu GPUInfo) error {
	paths, err := installPaths(root, kind, gpu)
	if err != nil {
		return err
	}
	for name, path := range map[string]string{"python": paths.Python, "worker": paths.Worker} {
		st, err := os.Stat(path)
		if err != nil || st.IsDir() || st.Size() == 0 {
			if err != nil {
				return fmt.Errorf("%s chưa sẵn sàng: %w", name, err)
			}
			return fmt.Errorf("%s chưa sẵn sàng", name)
		}
	}
	if err := os.MkdirAll(paths.ModelCache, 0o755); err != nil {
		return err
	}
	b, err := os.ReadFile(runtimeManifestPath(root, kind))
	if err != nil {
		return fmt.Errorf("thiếu manifest OCR: %w", err)
	}
	var got installManifest
	if err := json.Unmarshal(b, &got); err != nil {
		return fmt.Errorf("manifest OCR lỗi: %w", err)
	}
	want, err := expectedInstallManifest(kind, gpu)
	if err != nil {
		return err
	}
	if got != want {
		return fmt.Errorf("manifest OCR không đúng phiên bản")
	}
	wb, err := os.ReadFile(paths.Worker)
	if err != nil {
		return err
	}
	sum := sha256.Sum256(wb)
	if hex.EncodeToString(sum[:]) != want.WorkerSHA256 {
		return errors.New("worker OCR sai checksum")
	}
	return nil
}

func (m *Manager) cleanupLegacy() error {
	legacy := filepath.Join(filepath.Dir(m.Root), "RapidOCR")
	if filepath.Clean(legacy) == filepath.Clean(m.Root) {
		return nil
	}
	if _, err := os.Stat(legacy); !errors.Is(err, os.ErrNotExist) {
		if err := os.RemoveAll(legacy); err != nil {
			return err
		}
	}
	// RC8 and earlier used one CPU-only venv directly under Tools/OCR. Remove it
	// only after an RC9 runtime has reached ready state.
	_ = os.RemoveAll(filepath.Join(m.Root, "venv"))
	_ = os.Remove(filepath.Join(m.Root, installManifestName))
	return nil
}

func (m *Manager) resetRuntimeForRepair(kinds ...string) {
	for _, kind := range kinds {
		if kind == DeviceCPU || kind == DeviceGPU {
			_ = os.RemoveAll(filepath.Join(m.Root, "runtime", kind))
		}
	}
	_ = os.RemoveAll(filepath.Join(m.Root, "models"))
}
