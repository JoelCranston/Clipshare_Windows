module omaclip-peer

go 1.26.0

require github.com/rhemvi/omaclip v0.10.0

require (
	github.com/hashicorp/mdns v1.0.7 // indirect
	github.com/miekg/dns v1.1.72 // indirect
	golang.org/x/crypto v0.55.0 // indirect
	golang.org/x/image v0.45.0 // indirect
	golang.org/x/mod v0.39.0 // indirect
	golang.org/x/net v0.58.0 // indirect
	golang.org/x/sync v0.22.0 // indirect
	golang.org/x/sys v0.47.0 // indirect
	golang.org/x/tools v0.48.0 // indirect
)

// tests/interop/run.sh clones omaclip here.
replace github.com/rhemvi/omaclip => ../.omaclip
