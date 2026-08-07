# `ros_tcp_endpoint` — `send_topic_list` crash

A bug in the installed `ros_tcp_endpoint` that takes down the whole ROS↔Unity connection
whenever Unity asks for the topic list. Relevant because XRViz's Topic Browser
(`TopicBrowserUI`) makes exactly that request.

**Symptom**

```
AttributeError: 'NoneType' object has no attribute 'msg'
  File "ros_tcp_endpoint/tcp_sender.py", line 152, in <listcomp>
    else self.parse_message_name(node.msg)
```

The raise kills the client thread, which drops the TCP connection (`[Errno 9] Bad file
descriptor`). Unity reconnects, re-registers, requests the list again, and the loop repeats
every few seconds — so **no topic ever delivers data**, not just the one you were browsing for.

## The bug

`tcp_sender.py`, `send_topic_list()`:

```python
for i in topics_and_types:
    node = self.get_registered_topic(i[0])        # node belongs to topic `i`
    if len(i[1]) > 1:
        if node is not None:
            ... warning ...
    topic_list.types = [                          # rebuilt on EVERY outer iteration
        item[1][0].replace("/msg/", "/")
        if (len(item[1]) <= 1)
        else self.parse_message_name(node.msg)    # `node` is from the OUTER loop, not `item`
        for item in topics_and_types
    ]
```

Two defects:

1. **`node` is the wrong variable.** Inside the comprehension it refers to the outer loop's
   topic `i`, not to `item`. So the type reported for a multi-type topic is looked up from
   whatever unrelated topic the outer loop happens to be on.
2. **No `None` guard.** `get_registered_topic()` returns `None` for any topic Unity has not
   registered — which is most of them. The moment *any* topic in the graph has more than one
   type, the `else` branch runs with `node is None` and raises.

The assignment also sits inside the loop, so the whole list is rebuilt once per topic — O(n²)
for no reason.

**What triggers it here:** `/laser_scan` is advertised with more than one type, which is what
`len(item[1]) > 1` detects. That alone is enough to crash every request.

## Patch

Replace the body of `send_topic_list` with:

```python
    def send_topic_list(self):
        if self.queue is not None:
            topic_list = SysCommand_TopicsResponse()
            topics_and_types = self.tcp_server.get_topic_names_and_types()
            topic_list.topics = [item[0] for item in topics_and_types]

            types = []
            for item in topics_and_types:
                topic_name, topic_types = item[0], item[1]

                if len(topic_types) == 0:
                    types.append("")
                    continue

                if len(topic_types) == 1:
                    types.append(topic_types[0].replace("/msg/", "/"))
                    continue

                # More than one type on this topic: prefer whatever Unity subscribed as,
                # but only if Unity actually registered it - get_registered_topic returns
                # None for every other topic in the graph
                node = self.get_registered_topic(topic_name)
                if node is not None:
                    subscribed_type = self.parse_message_name(node.msg)
                    self.tcp_server.get_logger().warning(
                        "Only one message type per topic is supported, but found multiple "
                        "types for topic {}; maintaining {} as the subscribed type.".format(
                            topic_name, subscribed_type
                        )
                    )
                    types.append(subscribed_type)
                else:
                    types.append(topic_types[0].replace("/msg/", "/"))

            topic_list.types = types

            serialized_bytes = ClientThread.serialize_command("__topic_list", topic_list)
            self.queue.put(serialized_bytes)
```

Fixes the scoping, guards the `None`, handles a zero-type topic, and builds the list once.

File (adjust for your workspace):

```
~/ros2_ws/install/ros_tcp_endpoint/lib/python3.10/site-packages/ros_tcp_endpoint/tcp_sender.py
```

Patch the `src/` copy too, or a rebuild will overwrite it. Restart the endpoint afterwards.

## Also worth fixing: `/laser_scan` has multiple types

The patch stops the crash, but a topic carrying two message types is a genuine
misconfiguration and will give you confusing results either way. Check with:

```bash
ros2 topic info /laser_scan --verbose
```

That lists every publisher and subscriber with its type. Usually it's a stale node from a
previous run still holding the topic, or two drivers publishing the same name with different
types.

## Notes for the Unity side

- The endpoint **already normalises** ROS 2 names — `item[1][0].replace("/msg/", "/")` — so
  the list arrives in ROS 1 form (`sensor_msgs/LaserScan`). `TopicBrowserUI.NormalizeMessageName`
  is therefore belt-and-braces rather than required; it still guards the
  `parse_message_name(node.msg)` path, which does not go through that `replace`.
- `topic_list.topics` is built from `get_topic_names_and_types()`, i.e. the **full ROS graph** —
  so this is a genuine discovery API, not merely a list of what Unity registered.
- Until the endpoint is patched, untick **Refresh On Open** on `TopicBrowserUI` so that merely
  opening the panel doesn't take the connection down.
