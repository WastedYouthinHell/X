# Running Behind a Reverse Proxy

## NGINX

Because slskd uses websockets in addition to standard HTTP traffic, a couple of headers need to be defined along with the standard `proxy_pass` to enable everything to work properly.

Assuming a default NGINX installation, create `/etc/nginx/conf.d/slskd.conf` and populate it with one of the configurations below, depending on whether you'd like to serve slskd from a subdirectory or the root of the webserver.

### At a Subdirectory

In this scenario `URL_BASE` must be set to `/slskd`

```
server {
        listen 8080;

        location /slskd {
                proxy_pass http://localhost:5030;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection "Upgrade";
                proxy_set_header Host $host;
        }
}
```

### At the Root

In this scenario `URL_BASE` is left to the default value of `/`

```
server {
        listen 8080;

        location / {
                proxy_pass http://localhost:5030;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection "Upgrade";
                proxy_set_header Host $host;
        }
}
```

## Apache

With `URL_BASE` set to `/slskd`:

```
ProxyPass /slskd/ http://the.local.ip.address:5030/slskd/ upgrade=websocket
ProxyPassReverse /slskd/ http://the.local.ip.address:5030/slskd/

```

From [discussion #890](https://github.com/slskd/slskd/discussions/890)

## IIS (Windows)

You'll need the [URL rewrite module](https://learn.microsoft.com/en-us/iis/extensions/url-rewrite-module/using-the-url-rewrite-module) installed before you begin, and you'll need to set `URL_BASE` to `/slskd` in the slskd config.

Update the `web.config` for your root site to add a rewrite rule.  Here's an example that rewrites `<your site>/slskd`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
        <rules>
	    <rule name="slskd" stopProcessing="true">
                <match url="slskd(/.*)" />
                <action type="Rewrite" url="http://<ip of slskd>:<port of slskd>/slskd{R:1}" />
            </rule>
        </rules>
    </rewrite>
  </system.webServer>
</configuration>
```
