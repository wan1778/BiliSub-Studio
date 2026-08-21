package application

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"regexp"
	"strings"
	"time"
)

const bugReportEndpoint = "https://script.google.com/macros/s/AKfycbwQzULsUQZrsXw7BjuM8eMYUwKUQBAKYd1ALKGoy_JT_2JB_aBplW3MVK83InSrkWLDrw/exec"

var (
	secretKVPattern    = regexp.MustCompile(`(?i)(SESSDATA|bili_jct|buvid\d*|DedeUserID|X-BiliSub-Token|authorization|token|cookie)\s*[:=]\s*([^\s;&]+)`)
	userPathPattern    = regexp.MustCompile(`(?i)C:\\Users\\[^\\\s]+`)
	querySecretPattern = regexp.MustCompile(`(?i)([?&](?:token|auth|cookie|sessdata)=)[^&#\s]+`)
)

type BugReport struct {
	ID        string            `json:"id"`
	Version   string            `json:"version"`
	CreatedAt string            `json:"created_at"`
	Page      string            `json:"page"`
	Note      string            `json:"note"`
	Video     map[string]string `json:"video,omitempty"`
	Logs      map[string]string `json:"logs,omitempty"`
	Runtime   string            `json:"runtime"`
}

func SanitizeDiagnosticText(s string) string {
	s = secretKVPattern.ReplaceAllString(s, "$1=[ĐÃ ẨN]")
	s = userPathPattern.ReplaceAllString(s, `C:\Users\[ĐÃ ẨN]`)
	s = querySecretPattern.ReplaceAllString(s, "$1[ĐÃ ẨN]")
	return s
}

func NewBugID(now time.Time) string {
	return fmt.Sprintf("BUG-%s-%06d", now.Format("20060102-150405"), now.UnixNano()%1_000_000)
}

func (a *App) SendBugReport(ctx context.Context, r BugReport) error {
	if a == nil || a.State == nil {
		return fmt.Errorf("application chưa sẵn sàng")
	}
	if strings.TrimSpace(r.ID) == "" {
		r.ID = NewBugID(time.Now())
	}
	if strings.TrimSpace(r.Version) == "" {
		r.Version = a.State.Version
	}
	if strings.TrimSpace(r.CreatedAt) == "" {
		r.CreatedAt = time.Now().UTC().Format(time.RFC3339)
	}
	r.Note = truncateDiagnostic(SanitizeDiagnosticText(r.Note), 4000)
	for k, v := range r.Video {
		r.Video[k] = truncateDiagnostic(SanitizeDiagnosticText(v), 4000)
	}
	for k, v := range r.Logs {
		r.Logs[k] = truncateDiagnostic(SanitizeDiagnosticText(v), 30000)
	}
	r.Runtime = "native-windows-x64"
	body, err := json.Marshal(r)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, bugReportEndpoint, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "text/plain;charset=utf-8")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 400 {
		return fmt.Errorf("gửi báo lỗi HTTP %d", resp.StatusCode)
	}
	return nil
}

func truncateDiagnostic(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[len(s)-n:]
}
