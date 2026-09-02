# Resource Broker (phase 2)

Intentionally empty in phase 1. This is where `ResourceBroker`, `LeaseManager`,
and `JobQueue` will live once we add the first heavy, contended service
(Minecraft). Designing the lease model now - before there's a second real
consumer of it - would mean guessing at requirements. Samba/NFS in phase 1
are cheap enough not to need leasing at all.
