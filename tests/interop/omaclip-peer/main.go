// Command omaclip-peer runs omaclip's own sync code (server, fetcher and mDNS discovery, taken
// unchanged from github.com/rhemvi/omaclip) so the Windows client can be tested against it.
//
//	omaclip-peer serve    <passphrase> <port>   serve two entries and advertise over mDNS
//	omaclip-peer fetch    <passphrase> <host:port>
//	omaclip-peer discover <passphrase> <seconds>
package main

import (
	"context"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"time"

	"github.com/rhemvi/omaclip/app/handlers"
	"github.com/rhemvi/omaclip/business/clipboard"
	"github.com/rhemvi/omaclip/business/passphrase"
	"github.com/rhemvi/omaclip/business/peersclipsync"
	bsync "github.com/rhemvi/omaclip/business/sync"
	fmdns "github.com/rhemvi/omaclip/foundation/mdns"
	"github.com/rhemvi/omaclip/foundation/tlscert"
)

type staticMonitor struct {
	manifest clipboard.Manifest
	entries  map[string]clipboard.ClipboardEntry
}

func (m *staticMonitor) GetChecksum() string             { return m.manifest.Checksum }
func (m *staticMonitor) GetManifest() clipboard.Manifest { return m.manifest }
func (m *staticMonitor) GetEntry(id string) (clipboard.ClipboardEntry, bool) {
	e, ok := m.entries[id]
	return e, ok
}

type staticPeers []fmdns.Peer

func (p staticPeers) Peers() []fmdns.Peer { return p }

func sum(data []byte) string {
	s := sha256.Sum256(data)
	return hex.EncodeToString(s[:])
}

func main() {
	if len(os.Args) != 4 {
		fmt.Fprintln(os.Stderr, "usage: omaclip-peer serve|fetch|discover <passphrase> <arg>")
		os.Exit(2)
	}
	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	ps := &passphrase.Store{}
	ps.Set(os.Args[2])
	_, caCert, err := tlscert.GenerateCA(ps.KeyBytes())
	must(err)
	pool := x509.NewCertPool()
	pool.AddCert(caCert)

	switch os.Args[1] {
	case "serve":
		caTLS, _, err := tlscert.GenerateCA(ps.KeyBytes())
		must(err)
		leaf, err := tlscert.GenerateLeaf(caCert, caTLS.PrivateKey)
		must(err)

		png := []byte{0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3}
		text := "hello from omaclip <&> ✓"
		now := time.Date(2026, 11, 21, 12, 0, 0, 123456789, time.UTC)
		entries := []clipboard.ClipboardEntry{
			{ID: "1795000000000000002", Checksum: sum([]byte(text)), Content: text, ContentType: "text", Timestamp: now},
			{ID: "1795000000000000001", Checksum: sum(png), ContentType: "image", ImageMimeType: "image/png",
				ImageData: base64.StdEncoding.EncodeToString(png), Timestamp: now.Add(-time.Second)},
		}
		mon := &staticMonitor{entries: map[string]clipboard.ClipboardEntry{}}
		for _, e := range entries {
			mon.entries[e.ID] = e
			mon.manifest.Entries = append(mon.manifest.Entries, clipboard.ManifestEntry{
				ID: e.ID, Checksum: e.Checksum, ContentType: e.ContentType, ImageMimeType: e.ImageMimeType, Timestamp: e.Timestamp})
		}
		mon.manifest.Checksum = clipboard.ManifestChecksum(mon.manifest.Entries)

		port, _ := strconv.Atoi(os.Args[3])
		server := bsync.New(log, leaf, port)
		h := &handlers.ClipboardHandler{Monitor: mon, PassphraseStore: ps}
		auth := handlers.RequirePassphrase
		server.Handle("GET /api/clipboard/checksum", auth(ps, h.GetClipboardChecksum))
		server.Handle("GET /api/clipboard", auth(ps, h.GetClipboard))
		server.Handle("GET /api/clipboard/{id}/content", auth(ps, h.GetClipboardContent))
		must(server.Start())

		disc, err := fmdns.New(log, 2*time.Second, "omaclip-interop", ps, "")
		must(err)
		if err := disc.Register(server.Port()); err != nil {
			fmt.Fprintln(os.Stderr, "mdns:", err)
		}
		fmt.Printf("listening %d\n", server.Port())
		ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
		defer stop()
		<-ctx.Done()
		disc.Shutdown()

	case "fetch":
		host, portText, _ := strings.Cut(os.Args[3], ":")
		port, _ := strconv.Atoi(portText)
		f := peersclipsync.New(log, staticPeers{{Name: "windows", Addr: host, Port: port}}, time.Second, ps, pool)
		done := make(chan struct{}, 1)
		f.OnUpdate = func() { done <- struct{}{} }
		f.Start(context.Background())
		select {
		case <-done:
		case <-time.After(10 * time.Second):
			fmt.Println("timeout: no entries fetched")
			os.Exit(1)
		}
		for _, pc := range f.GetAll() {
			for _, e := range pc.Entries {
				if e.ContentType == "image" {
					fmt.Printf("%s image %s %d-bytes-base64 %s\n", pc.PeerName, e.ImageMimeType, len(e.ImageData), e.Timestamp.Format(time.RFC3339Nano))
				} else {
					fmt.Printf("%s text %q %s\n", pc.PeerName, e.Content, e.Timestamp.Format(time.RFC3339Nano))
				}
			}
		}

	case "discover":
		seconds, _ := strconv.Atoi(os.Args[3])
		disc, err := fmdns.New(log, 2*time.Second, "omaclip-discover", ps, "")
		must(err)
		must(disc.Register(1))
		ctx, cancel := context.WithTimeout(context.Background(), time.Duration(seconds)*time.Second)
		defer cancel()
		disc.Start(ctx)
		<-ctx.Done()
		for _, p := range disc.Peers() {
			fmt.Printf("peer %s %s:%d\n", p.Name, p.Addr, p.Port)
		}
		disc.Shutdown()
	}
}

func must(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
