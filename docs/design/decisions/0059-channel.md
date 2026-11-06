# 0059. Channels: an unbounded queue, ends that count, and a close that drains

- Status: accepted (2026-11-06)
- Spec: `library.channel`, `concurrency.sendable`, `concurrency.shared-classes`,
  `concurrency.interleaving`

## Decision

`std::channel` declares one channel type and two ends:

```java unchecked
pub shared class Channel<T extends sendable> {
    pub Channel();
    pub Sender<T> sender();
    pub Receiver<T> receiver();
}
pub shared class Sender<T extends sendable> { pub void send(T value); }
pub shared class Receiver<T extends sendable> { pub T? receive(); }
```

- A channel is unbounded, so `send` never waits, whether a receiver is waiting or not
  ([0016](0016-channels-and-locks.md)).
- `send` moves its value into the queue. A unique value therefore changes threads, and its
  destructor runs on the thread that receives it. `send` panics if something the value owns is
  borrowed ([Moving and sharing](../../spec/19-concurrency.md#concurrency.sendable.moving)).
- The channel closes when the last `Sender` is released. A closed channel stays closed, and
  `sender()` panics with "cannot make a sender for a closed channel".
- `receive` waits until a value is queued or the channel closes. Once the channel has closed,
  `receive` returns the values still queued, in the order they were sent, and then null.
- If no `Receiver` is left when the channel closes, the close drops every queued value. A
  `receiver()` made afterwards finds the closed channel empty.
- Releasing the last `Receiver` does not close the channel: a `Sender` may still send, and a
  receiver made later reads what was queued.
- The `Channel` object is not an end. Only `Sender` values keep the channel open, so a program may
  release the `Channel` once it has made its ends.
- A channel that never had a `Sender` is open. A `receive` on it waits, since a `Sender` may yet be
  made from the `Channel`, and a program that never makes one waits forever, as the compiled
  program does. The step budget bounds work, not a wait.
- A channel is not cycle-collected. A queued value that refers back to a shared object holding one
  of the channel's ends needs a `Weak<T>` ([0054](0054-shared-classes.md)).
- The runtime holds the state in `wyl::channel::State<T>` on the global heap: a mutex, a condition
  variable, a `std::deque<T>`, and counts of the senders and receivers. `wyl::SyncState<T>` is a
  counted handle to that state, which the last handle releases. The runtime uses the operating
  system's mutex and condition variable; the one-lock rule governs Wylow locks, not the channel's
  own storage.

## Alternatives

- **A bounded channel.** `send` would then wait when the queue is full, which makes a second wait
  point and a second way for two threads to deadlock. 0016 settles on unbounded.
- **Closing when the last `Receiver` is released.** A `send` would then have no receiver to keep
  its value for, and a receiver made later would find the channel closed.
- **Closing when the `Channel` object is released.** A program that drops the channel after making
  its ends would close it at a point it did not choose, and the ends would outlive the close.
- **Dropping queued values whenever the channel closes.** A receiver that still lives would lose
  the values it has not read yet, and a producer that closes before a consumer drains would lose
  them too. Draining until null is what makes a close a clean end of the stream.
- **Reopening a closed channel with a new `Sender`.** A receiver may already have read null, and
  values sent after that would be ones it cannot see. The panic reports the mistake at the
  `sender()` call.
- **A `Channel` that holds its ends in fields.** The ends would then have to be made in the
  constructor, and the channel could not be handed to another thread before its ends exist.

## Why

A channel is the first type through which one thread observes another, so it must answer three
questions that the rest of the concurrency rules leave to the type: when it closes, what a `send`
does when nobody will receive, and what a `receive` returns at the end. The last `Sender` closing
the channel comes from 0016, and it makes the producer the side that ends the stream. Draining
before null lets a consumer take every value the producer sent without ordering the two threads
more than the language already does.

Only `Sender` values count because the `Channel` is the factory: a program makes its ends, hands
them out, and releases the channel. Counting the `Channel` object would keep the stream open for as
long as the factory lives, which is an unrelated lifetime.

The `Sender` destructor is what closes the channel, so the close runs on the thread that releases
the last sender, and a queued value dropped there is destroyed there. The receiving thread destroys
the values it takes, which is what makes a unique value's destructor run on the thread that owns it.
