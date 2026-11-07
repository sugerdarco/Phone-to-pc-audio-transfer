// Phone to PC Audio - Windows 11 native receiver. No .NET dependency.
// Build: GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags="-H windowsgui -s -w" -o PhoneToPC-Audio-Windows.exe main.go
// Protocol: AWA1 + int32 session (LE) + int32 sequence (LE) + 960 bytes stereo 48 kHz 16-bit PCM.
package main

import (
	"encoding/binary"
	"fmt"
	"net"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"
)

const (
	port               = 45999
	frameSize          = 960
	framesPerBlock     = 4
	prefill            = 16
	maxQueued          = 60
	whdrDone           = 1
	wmCreate           = 0x0001
	wmDestroy          = 0x0002
	wmClose            = 0x0010
	wmCommand          = 0x0111
	wmTimer            = 0x0113
	wsOverlappedWindow = 0x00CF0000
	wsVisible          = 0x10000000
	wsChild            = 0x40000000
	bsPushbutton       = 0
	swShow             = 5
	cwUseDefault       = 0x80000000
	idStart            = 1001
	idStop             = 1002
	idVolumeDown       = 1003
	idVolumeUp         = 1004
)

var (
	user32                 = syscall.NewLazyDLL("user32.dll")
	kernel32               = syscall.NewLazyDLL("kernel32.dll")
	winmm                  = syscall.NewLazyDLL("winmm.dll")
	registerClassEx        = user32.NewProc("RegisterClassExW")
	createWindowEx         = user32.NewProc("CreateWindowExW")
	defWindowProc          = user32.NewProc("DefWindowProcW")
	destroyWindow          = user32.NewProc("DestroyWindow")
	postQuitMessage        = user32.NewProc("PostQuitMessage")
	getMessage             = user32.NewProc("GetMessageW")
	translateMessage       = user32.NewProc("TranslateMessage")
	dispatchMessage        = user32.NewProc("DispatchMessageW")
	showWindow             = user32.NewProc("ShowWindow")
	updateWindow           = user32.NewProc("UpdateWindow")
	setWindowText          = user32.NewProc("SetWindowTextW")
	setTimer               = user32.NewProc("SetTimer")
	loadCursor             = user32.NewProc("LoadCursorW")
	getModuleHandle        = kernel32.NewProc("GetModuleHandleW")
	waveOutOpen            = winmm.NewProc("waveOutOpen")
	waveOutPrepareHeader   = winmm.NewProc("waveOutPrepareHeader")
	waveOutWrite           = winmm.NewProc("waveOutWrite")
	waveOutReset           = winmm.NewProc("waveOutReset")
	waveOutUnprepareHeader = winmm.NewProc("waveOutUnprepareHeader")
	waveOutClose           = winmm.NewProc("waveOutClose")
)

type point struct{ X, Y int32 }
type msg struct {
	HWnd           uintptr
	Message        uint32
	_              uint32
	WParam, LParam uintptr
	Time           uint32
	Pt             point
	LPrivate       uint32
	_              uint32
}
type windowClass struct {
	Size, Style                        uint32
	WndProc                            uintptr
	ClsExtra, WndExtra                 int32
	Instance, Icon, Cursor, Background uintptr
	MenuName, ClassName                *uint16
	IconSmall                          uintptr
}
type waveFormat struct {
	FormatTag, Channels                  uint16
	SamplesPerSec, AvgBytesPerSec        uint32
	BlockAlign, BitsPerSample, ExtraSize uint16
}
type waveHeader struct {
	Data                        uintptr
	BufferLength, BytesRecorded uint32
	User                        uintptr
	Flags, Loops                uint32
	Next, Reserved              uintptr
}
type outputSlot struct {
	data   []byte
	header *waveHeader
}

type jitterBuffer struct {
	mu         sync.Mutex
	session    int32
	sessionSet bool
	lastPacket time.Time
	frames     map[int32][]byte
	next       int32
	hasNext    bool
}

func newJitterBuffer() *jitterBuffer { return &jitterBuffer{frames: make(map[int32][]byte)} }
func (j *jitterBuffer) clear()       { j.mu.Lock(); j.clearLocked(); j.mu.Unlock() }
func (j *jitterBuffer) clearLocked() {
	j.frames = make(map[int32][]byte)
	j.sessionSet = false
	j.hasNext = false
	j.lastPacket = time.Time{}
}
func (j *jitterBuffer) add(session, seq int32, pcm []byte) {
	if seq < 0 || len(pcm) != frameSize {
		return
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	now := time.Now()
	if !j.sessionSet || session != j.session || (!j.lastPacket.IsZero() && now.Sub(j.lastPacket) > time.Second) {
		j.clearLocked()
		j.sessionSet = true
		j.session = session
	}
	j.lastPacket = now
	if j.hasNext && seq < j.next {
		return
	}
	j.frames[seq] = append([]byte(nil), pcm...)
	if len(j.frames) > maxQueued {
		var max int32
		for k := range j.frames {
			if k > max {
				max = k
			}
		}
		skipTo := max - prefill + 1
		if skipTo < 0 {
			skipTo = 0
		}
		for k := range j.frames {
			if k < skipTo {
				delete(j.frames, k)
			}
		}
		j.next, j.hasNext = skipTo, true
	}
}
func (j *jitterBuffer) fill20ms(dest []byte, volume int) {
	clear(dest)
	j.mu.Lock()
	if !j.hasNext {
		if len(j.frames) < prefill {
			j.mu.Unlock()
			return
		}
		var min int32 = 2147483647
		for k := range j.frames {
			if k < min {
				min = k
			}
		}
		j.next, j.hasNext = min, true
	}
	for i := 0; i < framesPerBlock; i++ {
		if pcm, exists := j.frames[j.next]; exists {
			copy(dest[i*frameSize:(i+1)*frameSize], pcm)
			delete(j.frames, j.next)
		}
		if j.next == 2147483647 {
			j.next = 0
		} else {
			j.next++
		}
	}
	for k := range j.frames {
		if k < j.next {
			delete(j.frames, k)
		}
	}
	j.mu.Unlock()
	if volume >= 100 {
		return
	}
	if volume <= 0 {
		clear(dest)
		return
	}
	for i := 0; i < len(dest); i += 2 {
		val := int(int16(binary.LittleEndian.Uint16(dest[i:]))) * volume / 100
		binary.LittleEndian.PutUint16(dest[i:], uint16(int16(val)))
	}
}

type receiver struct {
	mu      sync.Mutex
	conn    *net.UDPConn
	quit    chan struct{}
	work    sync.WaitGroup
	jitter  *jitterBuffer
	running atomic.Bool
	volume  atomic.Int32
	packets atomic.Uint64
	peer    atomic.Value
	err     atomic.Value
}

func newReceiver() *receiver {
	r := &receiver{jitter: newJitterBuffer()}
	r.volume.Store(80)
	r.peer.Store("No phone connected")
	r.err.Store("")
	return r
}
func (r *receiver) start() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.running.Load() {
		return nil
	}
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4zero, Port: port})
	if err != nil {
		r.err.Store("Could not listen: " + err.Error())
		return err
	}
	r.conn, r.quit = conn, make(chan struct{})
	r.packets.Store(0)
	r.peer.Store("Waiting for Android")
	r.err.Store("")
	r.jitter.clear()
	r.running.Store(true)
	r.work.Add(2)
	go r.receiveLoop(conn)
	go r.audioLoop(r.quit)
	return nil
}
func (r *receiver) stop() {
	r.mu.Lock()
	if !r.running.Swap(false) {
		r.mu.Unlock()
		return
	}
	close(r.quit)
	_ = r.conn.Close()
	r.mu.Unlock()
	r.work.Wait()
	r.jitter.clear()
	r.peer.Store("Stopped")
}
func (r *receiver) receiveLoop(conn *net.UDPConn) {
	defer r.work.Done()
	buf := make([]byte, 2048)
	for r.running.Load() {
		n, from, err := conn.ReadFromUDP(buf)
		if err != nil {
			if r.running.Load() {
				r.err.Store("Network: " + err.Error())
			}
			return
		}
		if n != 12+frameSize || string(buf[:4]) != "AWA1" {
			continue
		}
		session := int32(binary.LittleEndian.Uint32(buf[4:8]))
		seq := int32(binary.LittleEndian.Uint32(buf[8:12]))
		r.jitter.add(session, seq, buf[12:n])
		r.peer.Store(from.IP.String())
		r.packets.Add(1)
	}
}
func waveError(stage string, ret uintptr) error {
	if ret == 0 {
		return nil
	}
	return fmt.Errorf("%s failed, WinMM error %d", stage, ret)
}
func (r *receiver) audioLoop(quit <-chan struct{}) {
	defer r.work.Done()
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	format := waveFormat{FormatTag: 1, Channels: 2, SamplesPerSec: 48000,
		AvgBytesPerSec: 48000 * 4, BlockAlign: 4, BitsPerSample: 16}
	var device uintptr
	rc, _, _ := waveOutOpen.Call(uintptr(unsafe.Pointer(&device)), 0xffffffff, uintptr(unsafe.Pointer(&format)), 0, 0, 0)
	if err := waveError("Open default audio device", rc); err != nil {
		r.err.Store(err.Error())
		return
	}
	slots := make([]outputSlot, 6)
	defer func() {
		waveOutReset.Call(device)
		for i := range slots {
			if slots[i].header != nil {
				waveOutUnprepareHeader.Call(device, uintptr(unsafe.Pointer(slots[i].header)), uintptr(unsafe.Sizeof(waveHeader{})))
			}
		}
		waveOutClose.Call(device)
		runtime.KeepAlive(slots)
	}()
	for i := range slots {
		slots[i].data = make([]byte, frameSize*framesPerBlock)
		slots[i].header = &waveHeader{
			Data:         uintptr(unsafe.Pointer(&slots[i].data[0])),
			BufferLength: uint32(len(slots[i].data)),
		}
		h := uintptr(unsafe.Pointer(slots[i].header))
		rc, _, _ = waveOutPrepareHeader.Call(device, h, uintptr(unsafe.Sizeof(waveHeader{})))
		if err := waveError("Prepare audio", rc); err != nil {
			r.err.Store(err.Error())
			return
		}
		rc, _, _ = waveOutWrite.Call(device, h, uintptr(unsafe.Sizeof(waveHeader{})))
		if err := waveError("Play audio", rc); err != nil {
			r.err.Store(err.Error())
			return
		}
	}
	for r.running.Load() {
		select {
		case <-quit:
			return
		default:
		}
		didWrite := false
		for i := range slots {
			s := &slots[i]
			if s.header.Flags&whdrDone == 0 {
				continue
			}
			r.jitter.fill20ms(s.data, int(r.volume.Load()))
			rc, _, _ = waveOutWrite.Call(device, uintptr(unsafe.Pointer(s.header)), uintptr(unsafe.Sizeof(waveHeader{})))
			if err := waveError("Queue audio", rc); err != nil {
				r.err.Store(err.Error())
				return
			}
			didWrite = true
		}
		if !didWrite {
			time.Sleep(3 * time.Millisecond)
		}
	}
}

var app = newReceiver()
var statusHandle, ipHandle, volumeHandle uintptr

func u16(s string) *uint16 { return syscall.StringToUTF16Ptr(s) }
func setText(h uintptr, s string) {
	if h != 0 {
		setWindowText.Call(h, uintptr(unsafe.Pointer(u16(s))))
	}
}
func addControl(parent uintptr, cls, value string, x, y, w, h, id int32) uintptr {
	inst, _, _ := getModuleHandle.Call(0)
	handle, _, _ := createWindowEx.Call(0, uintptr(unsafe.Pointer(u16(cls))), uintptr(unsafe.Pointer(u16(value))),
		wsVisible|wsChild|bsPushbutton, uintptr(x), uintptr(y), uintptr(w), uintptr(h), parent,
		uintptr(id), inst, 0)
	return handle
}
func ips() string {
	ints, err := net.Interfaces()
	if err != nil {
		return "Check ipconfig"
	}
	var result []string
	for _, in := range ints {
		if in.Flags&net.FlagUp == 0 || in.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, err := in.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			ip, _, e := net.ParseCIDR(a.String())
			if e == nil && ip.To4() != nil && !ip.IsLoopback() {
				result = append(result, ip.String())
			}
		}
	}
	if len(result) == 0 {
		return "No LAN address detected"
	}
	return strings.Join(result, ", ")
}
func refresh() {
	if app.running.Load() {
		msg := fmt.Sprintf("Listening on UDP %d  |  Phone: %s  |  Packets: %d", port, app.peer.Load().(string), app.packets.Load())
		if er := app.err.Load().(string); er != "" {
			msg = "Error: " + er
		}
		setText(statusHandle, msg)
	} else {
		if er := app.err.Load().(string); er != "" {
			setText(statusHandle, "Error: "+er)
		} else {
			setText(statusHandle, "Receiver stopped. Click Start to listen.")
		}
	}
	setText(volumeHandle, fmt.Sprintf("Phone audio volume: %d%%  (other Windows apps unaffected)", app.volume.Load()))
}
func wndProc(hwnd uintptr, message uint32, wparam, lparam uintptr) uintptr {
	switch message {
	case wmCreate:
		addControl(hwnd, "STATIC", "1. Connect phone and PC to the same Wi-Fi network.", 18, 16, 465, 20, 0)
		addControl(hwnd, "STATIC", "2. Enter this Windows IP in the Android app:", 18, 47, 465, 20, 0)
		ipHandle = addControl(hwnd, "STATIC", ips(), 32, 74, 453, 24, 0)
		addControl(hwnd, "STATIC", "3. Start streaming on Android. Phone and PC audio will mix.", 18, 109, 465, 22, 0)
		addControl(hwnd, "BUTTON", "Start receiver", 18, 148, 140, 34, idStart)
		addControl(hwnd, "BUTTON", "Stop receiver", 169, 148, 140, 34, idStop)
		addControl(hwnd, "BUTTON", "Volume  -", 18, 204, 105, 32, idVolumeDown)
		addControl(hwnd, "BUTTON", "Volume  +", 136, 204, 105, 32, idVolumeUp)
		volumeHandle = addControl(hwnd, "STATIC", "", 257, 210, 240, 30, 0)
		statusHandle = addControl(hwnd, "STATIC", "", 18, 259, 480, 35, 0)
		setTimer.Call(hwnd, 1, 500, 0)
		_ = app.start()
		refresh()
		return 0
	case wmCommand:
		switch uint16(wparam & 0xffff) {
		case idStart:
			_ = app.start()
		case idStop:
			app.stop()
		case idVolumeDown:
			v := app.volume.Load() - 10
			if v < 0 {
				v = 0
			}
			app.volume.Store(v)
		case idVolumeUp:
			v := app.volume.Load() + 10
			if v > 100 {
				v = 100
			}
			app.volume.Store(v)
		}
		refresh()
		return 0
	case wmTimer:
		refresh()
		return 0
	case wmClose:
		app.stop()
		destroyWindow.Call(hwnd)
		return 0
	case wmDestroy:
		postQuitMessage.Call(0)
		return 0
	}
	ret, _, _ := defWindowProc.Call(hwnd, uintptr(message), wparam, lparam)
	return ret
}
func main() {
	// Win32 UI message pump must stay on the window-creating OS thread.
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	instance, _, _ := getModuleHandle.Call(0)
	className := u16("PhoneToPCReceiverWindow2026")
	cursor, _, _ := loadCursor.Call(0, 32512) // IDC_ARROW
	wc := windowClass{
		Size:     uint32(unsafe.Sizeof(windowClass{})),
		WndProc:  syscall.NewCallback(wndProc),
		Instance: instance, Cursor: cursor, Background: 6, // COLOR_WINDOW + 1
		ClassName: className,
	}
	if registered, _, _ := registerClassEx.Call(uintptr(unsafe.Pointer(&wc))); registered == 0 {
		panic("Cannot register receiver window")
	}
	hwnd, _, _ := createWindowEx.Call(0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(u16("Phone to PC Audio - Windows 11"))),
		wsOverlappedWindow|wsVisible, cwUseDefault, cwUseDefault, 535, 355, 0, 0, instance, 0)
	if hwnd == 0 {
		panic("Cannot create receiver window")
	}
	showWindow.Call(hwnd, swShow)
	updateWindow.Call(hwnd)
	var m msg
	for {
		value, _, _ := getMessage.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(value) <= 0 {
			break
		}
		translateMessage.Call(uintptr(unsafe.Pointer(&m)))
		dispatchMessage.Call(uintptr(unsafe.Pointer(&m)))
	}
}
