DSS MANUAL UPDATE PACKAGES

Place one official DSS package ZIP in this folder, then run DSS-ManualUpdate.cmd
from the application folder. Supported names are:

  DedicatedServerStudio-<version>-production.zip
  DedicatedServerStudio-<version>-nexus.zip

The manual updater verifies the package before replacing application files and
restarts the dashboard after installation. It keeps the selected ZIP here and
preserves DSS data, settings, server files, and logs.

This folder also stores immutable DSS application builds used during updates.
Do not remove folders whose names begin with a version number unless you know
they are no longer in use.
