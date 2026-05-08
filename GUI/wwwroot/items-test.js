!(function()
{
    'use strict'

    var statusEl = document.getElementById(`status`)
    var listEl = document.getElementById(`items`)

    fetch(`/api/items`)
        .then(x => x.ok
            ? x.json()
            : new Error(`HTTP ${response.status}`)
        )
        .then(items =>
        {
            statusEl.textContent = `${items.length} items`

            const fragment = document.createDocumentFragment()

            for (const item of items)
            {
                const li = document.createElement(`li`)
                
                const row = document.createElement(`div`)
                row.classList.add(`row`)
                li.appendChild(row)
                
                if (item.icon)
                {
                    var img = document.createElement(`img`)
                    img.src = item.icon
                    img.alt = item.name
                    img.loading = `lazy`
                    row.appendChild(img)
                }
                else
                {
                    var placeholder = document.createElement(`div`)
                    placeholder.className = `placeholder`
                    row.appendChild(placeholder)
                }
                
                const info = document.createElement(`div`)
                info.classList.add(`info`)
                row.appendChild(info)
                
                const name = document.createElement(`span`)
                name.classList.add(`name`)
                name.textContent = item.meta?.name ?? item.name
                info.appendChild(name)
                
                const description = document.createElement(`span`)
                description.classList.add(`description`)
                description.textContent = item.meta?.description ?? `-`
                info.appendChild(description)
                
                fragment.appendChild(li)
            }
            listEl.appendChild(fragment)
        })
        .catch(err =>
        {
            statusEl.textContent = `Failed to load items: ${err.message}`
        })
})()
