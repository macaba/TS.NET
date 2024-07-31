# SCPI interface

## Background

Refer to https://github.com/ngscopeclient/scopehal/wiki/SCPI-API-design for guidance.

Most of the commands below reflect the current command set which is open to change as it hasn't been carefully curated.

## Command set

| Command | Description | Format | Example | Notes |
| --- | --- | --- | --- | --- |
| `*IDN?` | Query the ID of the instrument | `[Model],[Model variant],[Serial number],[SCPI protocol version]` | `ThunderScope,Rev4.1 (TB),0123456789,1.0.0` | SCPI protocol version to follow semantic versioning, where major version change indicates incompatible protocol change, minor version change indicates new features and bug fixes. More detailed information, e.g. firmware versions or interface details, will be a separate command. |
| `RATES?` | Query the available sample rates. | | | |
| `RATE <Femtoseconds>` | | | | |
| `DEPTHS?` | Query the available sample counts. | | | |
| `DEPTH <Sample Count>` | | | | |
| `RUN` | | | | |
| `STOP` | | | | |
| `FORCE` | | | | |
| `SINGLE` | | | | |
| `NORMAL` | | | | |
| `AUTO` | | | | |
| `STREAM` | | | | |
| `TRIG:LEV <Volts>` | | | | |
| `TRIG:SOU <ChannelIndex>` | | | | |
| `TRIG:DELAY <Femtoseconds>` | | | | |
| `TRIG:EDGE:DIR <RISING:FALLING>` | | | | |
| `<ChannelIndex>:ON` | | | | |
| `<ChannelIndex>:OFF` | | | | |
| `<ChannelIndex>:COUP` | | | | |
| `<ChannelIndex>:OFFS` | | | | |
| `<ChannelIndex>:RANGE` | | | | |