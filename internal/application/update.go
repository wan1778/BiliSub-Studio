package application

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

const (
	stableManifestFileID = "1wpVgh6urUJYhX-b6nqAj3TOJ6wyaoB0C"
	betaManifestFileID   = "18gW_x8Y_jD-PMyk5kv7tXYF--qzsQDiT"
)

type updateManifest struct {
	Version     string   `json:"version"`
	DownloadURL string   `json:"download_url"`
	SHA256      string   `json:"sha256"`
	Size        int64    `json:"size"`
	SourceURL   string   `json:"source_url"`
	Notes       []string `json:"notes"`
}

type UpdateInfo struct {
	Current   string
	Latest    string
	Available bool
	Notes     []string
}

func (a *App) CheckUpdate(ctx context.Context) (UpdateInfo, error) {
	m, err := fetchManifest(ctx, a.State.Version)
	if err != nil {
		return UpdateInfo{}, err
	}
	return UpdateInfo{Current: a.State.Version, Latest: m.Version, Available: versionLess(a.State.Version, m.Version), Notes: append([]string(nil), m.Notes...)}, nil
}

func (a *App) PrepareUpdate(ctx context.Context) (string, string, error) {
	if a.Jobs.Active() {
		return "", "", errors.New("đang có tác vụ; hãy hoàn tất hoặc hủy trước khi cập nhật")
	}
	m, err := fetchManifest(ctx, a.State.Version)
	if err != nil {
		return "", "", err
	}
	if !versionLess(a.State.Version, m.Version) {
		return "", "", errors.New("BiliSub Studio đã là phiên bản mới nhất")
	}
	_ = a.OCR.Stop()
	path := filepath.Join(a.State.Paths.Temp, "BiliSubStudio_update_"+safeFile(m.Version)+".exe")
	if err := downloadUpdate(ctx, m, path); err != nil {
		return "", "", err
	}
	return path, m.Version, nil
}

func manifestFileIDForVersion(currentVersion string) string {
	if parseVersion(currentVersion).Pre != "" {
		return betaManifestFileID
	}
	return stableManifestFileID
}
func fetchManifest(ctx context.Context, currentVersion string) (updateManifest, error) {
	id := manifestFileIDForVersion(currentVersion)
	endpoint := "https://drive.google.com/uc?export=download&id=" + url.QueryEscape(id)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return updateManifest{}, err
	}
	req.Header.Set("User-Agent", "BiliSubStudio/4")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return updateManifest{}, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return updateManifest{}, fmt.Errorf("manifest HTTP %d", resp.StatusCode)
	}
	var m updateManifest
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&m); err != nil {
		return updateManifest{}, err
	}
	if strings.TrimSpace(m.Version) == "" || strings.TrimSpace(m.DownloadURL) == "" || strings.TrimSpace(m.SHA256) == "" || m.Size <= 0 {
		return updateManifest{}, errors.New("manifest update không hợp lệ")
	}
	return m, nil
}
func downloadUpdate(ctx context.Context, m updateManifest, path string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, m.DownloadURL, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "BiliSubStudio/4")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("update HTTP %d", resp.StatusCode)
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".part"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	h := sha256.New()
	n, cpErr := io.Copy(io.MultiWriter(f, h), io.LimitReader(resp.Body, m.Size+1))
	syncErr := f.Sync()
	closeErr := f.Close()
	if cpErr != nil || syncErr != nil || closeErr != nil || n != m.Size {
		_ = os.Remove(tmp)
		if cpErr != nil {
			return cpErr
		}
		if syncErr != nil {
			return syncErr
		}
		if closeErr != nil {
			return closeErr
		}
		return fmt.Errorf("update size %d, mong đợi %d", n, m.Size)
	}
	if got := hex.EncodeToString(h.Sum(nil)); !strings.EqualFold(got, strings.TrimSpace(m.SHA256)) {
		_ = os.Remove(tmp)
		return errors.New("SHA-256 bản cập nhật không khớp")
	}
	_ = os.Remove(path)
	return os.Rename(tmp, path)
}

type parsedVersion struct {
	Major, Minor, Patch int
	Pre                 string
}

func parseVersion(v string) parsedVersion {
	v = strings.TrimSpace(strings.TrimPrefix(strings.ToLower(v), "v"))
	parts := strings.SplitN(v, "-", 2)
	nums := strings.Split(parts[0], ".")
	out := parsedVersion{}
	if len(nums) > 0 {
		out.Major, _ = strconv.Atoi(nums[0])
	}
	if len(nums) > 1 {
		out.Minor, _ = strconv.Atoi(nums[1])
	}
	if len(nums) > 2 {
		out.Patch, _ = strconv.Atoi(nums[2])
	}
	if len(parts) > 1 {
		out.Pre = parts[1]
	}
	return out
}
func versionLess(a, b string) bool {
	x, y := parseVersion(a), parseVersion(b)
	if x.Major != y.Major {
		return x.Major < y.Major
	}
	if x.Minor != y.Minor {
		return x.Minor < y.Minor
	}
	if x.Patch != y.Patch {
		return x.Patch < y.Patch
	}
	return prereleaseLess(x.Pre, y.Pre)
}
func prereleaseLess(a, b string) bool {
	if a == b {
		return false
	}
	if a == "" {
		return false
	}
	if b == "" {
		return true
	}
	return a < b
}
