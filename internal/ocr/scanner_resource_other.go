//go:build !windows

package ocr

import "context"

// CI and non-Windows development hosts intentionally leave system CPU/RAM
// telemetry unknown. The Auto gate then falls back to the bounded benchmark
// watchdog. Windows production builds use native kernel32 memory/CPU metrics.
func probePlatformResources(context.Context) autoResourceSnapshot {
	return autoResourceSnapshot{}
}
