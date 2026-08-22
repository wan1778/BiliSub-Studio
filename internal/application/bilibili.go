package application

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"time"
)

type QRStartResult struct {
	URL string
	Key string
}

type QRPollResult struct {
	LoggedIn bool
	Message  string
	User     string
	Expired  bool
}

func (a *App) SetCookie(ctx context.Context, raw string) (string, error) {
	if err := a.State.SetCookie(raw); err != nil {
		return "", err
	}
	a.invalidateCookieStatus()
	ok, user, errMsg := a.cookieStatus(ctx, true)
	if !ok {
		if errMsg == "" {
			errMsg = "Cookie không hợp lệ hoặc đã hết hạn"
		}
		return "", fmt.Errorf("%s", errMsg)
	}
	return user, nil
}

func (a *App) DeleteCookie() error {
	if err := a.State.DeleteCookie(); err != nil {
		return err
	}
	a.invalidateCookieStatus()
	return nil
}

func (a *App) QRStart(ctx context.Context) (QRStartResult, error) {
	var resp struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			URL string `json:"url"`
			Key string `json:"qrcode_key"`
		} `json:"data"`
	}
	if _, err := biliJSONCookies(ctx, "https://passport.bilibili.com/x/passport-login/web/qrcode/generate", &resp); err != nil {
		return QRStartResult{}, err
	}
	if resp.Code != 0 || strings.TrimSpace(resp.Data.Key) == "" {
		return QRStartResult{}, fmt.Errorf("Bilibili QR: %d %s", resp.Code, resp.Message)
	}
	return QRStartResult{URL: resp.Data.URL, Key: resp.Data.Key}, nil
}

func (a *App) QRPoll(ctx context.Context, key string) (QRPollResult, error) {
	key = strings.TrimSpace(key)
	if key == "" {
		return QRPollResult{}, fmt.Errorf("thiếu qrcode key")
	}
	var resp struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			URL     string `json:"url"`
			Code    int    `json:"code"`
			Message string `json:"message"`
		} `json:"data"`
	}
	endpoint := "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + url.QueryEscape(key)
	setCookies, err := biliJSONCookies(ctx, endpoint, &resp)
	if err != nil {
		return QRPollResult{}, err
	}
	if resp.Code != 0 {
		return QRPollResult{}, fmt.Errorf("%s", resp.Message)
	}
	switch resp.Data.Code {
	case 0:
		cookie := cookieFromQR(resp.Data.URL, setCookies)
		if cookie == "" {
			return QRPollResult{}, fmt.Errorf("QR thành công nhưng không lấy được Cookie")
		}
		if err := a.State.SetCookie(cookie); err != nil {
			return QRPollResult{}, err
		}
		a.invalidateCookieStatus()
		ok, user, errMsg := a.cookieStatus(ctx, true)
		if !ok {
			if errMsg == "" {
				errMsg = "Bilibili chưa xác nhận trạng thái đăng nhập"
			}
			return QRPollResult{}, fmt.Errorf("%s", errMsg)
		}
		msg := "Đăng nhập thành công"
		if user != "" {
			msg += ": " + user
		}
		return QRPollResult{LoggedIn: true, Message: msg, User: user}, nil
	case 86101:
		return QRPollResult{Message: "Chưa quét mã QR"}, nil
	case 86090:
		return QRPollResult{Message: "Đã quét, hãy xác nhận trên điện thoại"}, nil
	case 86038:
		return QRPollResult{Message: "Mã QR đã hết hạn", Expired: true}, nil
	default:
		msg := strings.TrimSpace(resp.Data.Message)
		if msg == "" {
			msg = fmt.Sprintf("Bilibili QR code %d", resp.Data.Code)
		}
		return QRPollResult{Message: msg}, nil
	}
}

func biliJSONCookies(ctx context.Context, endpoint string, out any) ([]*http.Cookie, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36")
	req.Header.Set("Referer", "https://www.bilibili.com/")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("Bilibili HTTP %d", resp.StatusCode)
	}
	cookies := resp.Cookies()
	if err := json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(out); err != nil {
		return cookies, err
	}
	return cookies, nil
}

func cookieFromQR(raw string, setCookies []*http.Cookie) string {
	values := make(map[string]string)
	for _, c := range setCookies {
		if c == nil || strings.TrimSpace(c.Name) == "" || c.Value == "" {
			continue
		}
		values[c.Name] = c.Value
	}
	if u, err := url.Parse(raw); err == nil {
		q := u.Query()
		for _, k := range []string{"SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "b_nut", "buvid_fp", "buvid_fp_plain", "b_lsid"} {
			if v := q.Get(k); v != "" {
				values[k] = v
			}
		}
	}
	priority := []string{"SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "b_nut", "buvid_fp", "buvid_fp_plain", "b_lsid"}
	var out []string
	for _, k := range priority {
		if v := values[k]; v != "" {
			out = append(out, k+"="+v)
			delete(values, k)
		}
	}
	keys := make([]string, 0, len(values))
	for k := range values {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		out = append(out, k+"="+values[k])
	}
	return strings.Join(out, "; ")
}

type biliNavResponse struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		IsLogin bool   `json:"isLogin"`
		Uname   string `json:"uname"`
	} `json:"data"`
}

func validateBilibiliCookie(ctx context.Context, raw string) (bool, string, error) {
	if strings.TrimSpace(raw) == "" {
		return false, "", nil
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "https://api.bilibili.com/x/web-interface/nav", nil)
	if err != nil {
		return false, "", err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36")
	req.Header.Set("Referer", "https://www.bilibili.com/")
	req.Header.Set("Cookie", raw)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return false, "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return false, "", fmt.Errorf("Bilibili nav HTTP %d", resp.StatusCode)
	}
	var nav biliNavResponse
	if err := json.NewDecoder(io.LimitReader(resp.Body, 2<<20)).Decode(&nav); err != nil {
		return false, "", err
	}
	if nav.Code != 0 {
		return false, "", fmt.Errorf("Bilibili nav: %d %s", nav.Code, nav.Message)
	}
	return nav.Data.IsLogin, strings.TrimSpace(nav.Data.Uname), nil
}

func (a *App) invalidateCookieStatus() {
	a.cookieMu.Lock()
	a.cookieAt = time.Time{}
	a.cookieOK = false
	a.cookieUser = ""
	a.cookieErr = ""
	a.cookieMu.Unlock()
}

func (a *App) cookieStatus(ctx context.Context, force bool) (bool, string, string) {
	a.cookieMu.Lock()
	if !force && !a.cookieAt.IsZero() && time.Since(a.cookieAt) < 5*time.Minute {
		ok, user, errMsg := a.cookieOK, a.cookieUser, a.cookieErr
		a.cookieMu.Unlock()
		return ok, user, errMsg
	}
	a.cookieMu.Unlock()
	ok, user, err := validateBilibiliCookie(ctx, a.State.CookieValue())
	errMsg := ""
	if err != nil {
		errMsg = err.Error()
	} else if !ok {
		errMsg = "Cookie không hợp lệ hoặc đã hết hạn"
	}
	a.cookieMu.Lock()
	a.cookieAt, a.cookieOK, a.cookieUser, a.cookieErr = time.Now(), ok, user, errMsg
	a.cookieMu.Unlock()
	return ok, user, errMsg
}
